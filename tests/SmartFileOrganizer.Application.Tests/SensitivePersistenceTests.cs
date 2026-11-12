using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Ollama;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Scanning.Engine;
using SmartFileOrganizer.Scanning.Rules;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Markers through scan, SQLite and the model answer. Fake file system, temp database,
/// canned Ollama responses; no network, no real files.
/// </summary>
public class SensitivePersistenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseContext _db;
    private readonly ScanJobRepository _jobRepo;
    private readonly FileRepository _fileRepo;
    private readonly DirectoryRepository _dirRepo;

    public SensitivePersistenceTests()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _dbPath = Path.Combine(Path.GetTempPath(), $"sfo_sensitive_{Guid.NewGuid():N}.db");
        _db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        _db.InitializeAsync().GetAwaiter().GetResult();
        _jobRepo = new ScanJobRepository(_db);
        _fileRepo = new FileRepository(_db);
        _dirRepo = new DirectoryRepository(_db);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private static byte[] FakeKeyFile() => Encoding.ASCII.GetBytes(
        "-----BEGIN OPENSSH " + "PRIVATE KEY-----\nTHIS-IS-TEST-FILLER-NOT-A-KEY\n");

    private async Task<ScanJob> ScanAsync(FakeFileSystemAccessor fs)
    {
        var job = await _jobRepo.CreateAsync(new ScanJob { RootPath = "/Scan", Status = JobStatus.Created, CreatedAt = DateTime.UtcNow });
        var engine = new ScanEngine(fs, new HeuristicsEngine(ScanRulesFile.Default), _fileRepo, _dirRepo, _jobRepo, NullLogger<ScanEngine>.Instance);
        await engine.RunAsync(job, new Progress<ScanProgress>(_ => { }), CancellationToken.None);
        return job;
    }

    [Fact]
    public async Task Scan_StoresRuleMarkers_ForFilesTheModelNeverSees()
    {
        var fs = new FakeFileSystemAccessor(
            new Dictionary<string, string[]>
            {
                ["/Scan"] = ["/Scan/id_rsa", "/Scan/notizen", "/Scan/photo.jpg", "/Scan/readme.txt"]
            },
            new Dictionary<string, byte[]>
            {
                // No telling name, type Unknown: only the header rule can find it.
                ["/Scan/notizen"] = FakeKeyFile(),
                ["/Scan/readme.txt"] = Encoding.ASCII.GetBytes("nothing to see\n")
            });

        var job = await ScanAsync(fs);
        var files = (await _fileRepo.GetByDirectoryAsync(job.Id, "/Scan")).ToDictionary(f => f.Name);

        Assert.Equal(SensitiveMarker.Schluessel, files["id_rsa"].SensitiveMarkers);
        Assert.Equal("name:id_rsa", files["id_rsa"].SensitiveRule);
        Assert.Equal(SensitiveMarker.Schluessel, files["notizen"].SensitiveMarkers);
        Assert.Equal("header:private-key", files["notizen"].SensitiveRule);
        Assert.Equal(SensitiveMarker.None, files["readme.txt"].SensitiveMarkers);
        Assert.Null(files["readme.txt"].SensitiveRule);
        Assert.DoesNotContain("/Scan/photo.jpg", fs.PrefixReads);

        // Stored as the fixed tokens, not as a number.
        await using var conn = _db.CreateConnection();
        var raw = await conn.ExecuteScalarAsync<string>(
            "SELECT sensitive_markers FROM file_nodes WHERE name = 'id_rsa'");
        Assert.Equal("schluessel", raw);
    }

    [Fact]
    public async Task Export_OfScannedJob_ProtectsMarkedRows()
    {
        var fs = new FakeFileSystemAccessor(
            new Dictionary<string, string[]> { ["/Scan"] = ["/Scan/notizen", "/Scan/readme.txt"] },
            new Dictionary<string, byte[]> { ["/Scan/notizen"] = FakeKeyFile() });
        var job = await ScanAsync(fs);

        var notes = (await _fileRepo.GetByDirectoryAsync(job.Id, "/Scan")).Single(f => f.Name == "notizen");
        await new ClassificationRepository(_db).UpsertAsync(new FileClassification
        {
            FileNodeId = notes.Id,
            Category = FileCategory.Document,
            Summary = "MODEL-SUMMARY-QUOTING-THE-FILE",
            RawResponse = "RAW-RESPONSE-QUOTING-THE-FILE",
            AnalyzedAt = DateTime.UtcNow,
            IsAiResult = true
        });

        var sw = new StringWriter();
        await new ScanExportService(_dirRepo, _fileRepo).ExportJobAsync(job.Id, sw);
        var csv = sw.ToString();

        Assert.DoesNotContain("QUOTING-THE-FILE", csv);
        Assert.Contains("schluessel", csv);
    }

    [Fact]
    public async Task ClassificationRepository_RoundTripsModelMarkersAsTokens()
    {
        var job = await _jobRepo.CreateAsync(new ScanJob { RootPath = "/Scan", Status = JobStatus.Created, CreatedAt = DateTime.UtcNow });
        await _fileRepo.InsertBatchAsync([new FileNode
        {
            JobId = job.Id, FullPath = "/Scan/a.pdf", Name = "a.pdf", ParentPath = "/Scan", RootPath = "/Scan",
            RelativePath = "a.pdf", RelativeDir = ".", Extension = ".pdf", FileType = FileType.Document,
            LastWriteTime = DateTime.UtcNow, ScannedAt = DateTime.UtcNow
        }]);
        var file = (await _fileRepo.GetByDirectoryAsync(job.Id, "/Scan")).Single();

        var repo = new ClassificationRepository(_db);
        await repo.UpsertAsync(new FileClassification
        {
            FileNodeId = file.Id,
            AnalyzedAt = DateTime.UtcNow,
            SensitiveMarkers = SensitiveMarker.Finanzen | SensitiveMarker.Ausweis
        });

        var loaded = await repo.GetByFileNodeIdAsync(file.Id);
        Assert.Equal(SensitiveMarker.Finanzen | SensitiveMarker.Ausweis, loaded!.SensitiveMarkers);

        await using var conn = _db.CreateConnection();
        Assert.Equal("ausweis,finanzen", await conn.ExecuteScalarAsync<string>(
            "SELECT sensitive_markers FROM ai_results WHERE file_node_id = @id", new { id = file.Id }));

        // Unmarked is NULL, not an empty string or 0.
        await repo.UpsertAsync(new FileClassification { FileNodeId = file.Id, AnalyzedAt = DateTime.UtcNow });
        Assert.Null(await conn.ExecuteScalarAsync<string?>(
            "SELECT sensitive_markers FROM ai_results WHERE file_node_id = @id", new { id = file.Id }));
        Assert.Equal(SensitiveMarker.None, (await repo.GetByFileNodeIdAsync(file.Id))!.SensitiveMarkers);
    }

    // ------------------------------------------------------------------ model answer

    private sealed class CannedHandler(string content) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var body = JsonSerializer.Serialize(new { message = new { role = "assistant", content } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(FileClassification Result, string Request)> ClassifyWithAnswerAsync(string modelJson)
    {
        var handler = new CannedHandler(modelJson);
        var service = new OllamaService(new HttpClient(handler), new OllamaOptions(), NullLogger<OllamaService>.Instance);
        var result = await service.ClassifyAsync(new OllamaClassificationInput
        {
            File = new FileNode { Id = 1, Name = "scan.pdf", Extension = ".pdf", FileType = FileType.Document }
        });
        return (result, handler.LastRequestBody!);
    }

    [Fact]
    public async Task ModelAnswer_SensitiveArray_IsParsed()
    {
        var (result, _) = await ClassifyWithAnswerAsync(
            """{"category":"PersonalDocument","importance":"High","confidence":0.9,"summary":"Identity card scan","suggested_target":"Documents","sensitive":["ausweis","made_up"]}""");

        Assert.Null(result.Error);
        Assert.Equal(FileCategory.PersonalDocument, result.Category);
        Assert.Equal(SensitiveMarker.Ausweis, result.SensitiveMarkers);
    }

    [Fact]
    public async Task ModelAnswer_SensitiveAsString_IsParsed()
    {
        var (result, _) = await ClassifyWithAnswerAsync(
            """{"category":"Document","importance":"High","confidence":0.8,"summary":"x","suggested_target":"","sensitive":"zugangsdaten, finanzen"}""");
        Assert.Equal(SensitiveMarker.Zugangsdaten | SensitiveMarker.Finanzen, result.SensitiveMarkers);
    }

    [Theory]
    [InlineData("""{"category":"Photo","importance":"Low","confidence":0.9,"summary":"x","suggested_target":"","sensitive":[]}""")]
    [InlineData("""{"category":"Photo","importance":"Low","confidence":0.9,"summary":"x","suggested_target":""}""")]
    [InlineData("""{"category":"Photo","sensitive":42}""")]
    public async Task ModelAnswer_WithoutMarkers_IsNone(string answer)
    {
        var (result, _) = await ClassifyWithAnswerAsync(answer);
        Assert.Equal(SensitiveMarker.None, result.SensitiveMarkers);
        Assert.Equal(FileCategory.Photo, result.Category);
    }

    [Fact]
    public async Task Request_AsksForMarkersInTheSameCall()
    {
        var (_, request) = await ClassifyWithAnswerAsync("""{"category":"Photo"}""");

        using var doc = JsonDocument.Parse(request);
        var format = doc.RootElement.GetProperty("format");
        var items = format.GetProperty("properties").GetProperty("sensitive").GetProperty("items").GetProperty("enum");
        Assert.Equal(SensitiveMarkerNames.AllTokens, items.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray());
        Assert.Contains("sensitive", format.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }
}
