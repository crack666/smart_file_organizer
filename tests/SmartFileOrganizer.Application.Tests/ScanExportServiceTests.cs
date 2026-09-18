using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Scanning.Engine;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Tests for the CSV export. The escaping tests are the point of this file:
/// paths contain commas, AI summaries contain quotes and line breaks, and a summary
/// starting with '=' is a formula as far as Excel is concerned.
/// </summary>
public class ScanExportServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseContext _db;
    private readonly ScanJobRepository _jobRepo;
    private readonly FileRepository _fileRepo;
    private readonly DirectoryRepository _dirRepo;

    public ScanExportServiceTests()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        _dbPath = Path.Combine(Path.GetTempPath(), $"sfo_export_test_{Guid.NewGuid():N}.db");
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

    // ------------------------------------------------------------------ escaping

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("C:\\Fotos\\Urlaub", "C:\\Fotos\\Urlaub")]
    public void Escape_LeavesHarmlessValuesAlone(string? input, string expected)
        => Assert.Equal(expected, ScanExportService.Escape(input));

    [Fact]
    public void Escape_QuotesFieldsContainingComma()
        => Assert.Equal("\"Rechnung, final.pdf\"", ScanExportService.Escape("Rechnung, final.pdf"));

    [Fact]
    public void Escape_DoublesInnerQuotes()
        => Assert.Equal("\"a \"\"b\"\" c\"", ScanExportService.Escape("a \"b\" c"));

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\r\nline2")]
    public void Escape_QuotesFieldsContainingNewlines(string input)
    {
        var result = ScanExportService.Escape(input);
        Assert.StartsWith("\"", result);
        Assert.EndsWith("\"", result);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+49 170")]
    [InlineData("-summary")]
    [InlineData("@home")]
    public void Escape_NeutralisesFormulaStarts(string input)
    {
        // Excel would evaluate these. A leading apostrophe makes it text again.
        var result = ScanExportService.Escape(input).Trim('"');
        Assert.StartsWith("'", result);
    }

    // ------------------------------------------------------------------ rows

    [Fact]
    public void WriteHeader_MatchesDeclaredColumnOrder()
    {
        var sw = new StringWriter();
        ScanExportService.WriteHeader(sw);

        Assert.Equal(string.Join(',', ScanExportService.Columns) + "\r\n", sw.ToString());
    }

    [Fact]
    public void WriteRow_HasExactlyOneFieldPerColumn()
    {
        var sw = new StringWriter();
        ScanExportService.WriteRow(sw, MakeClassifiedFile());

        var fields = sw.ToString().TrimEnd('\r', '\n').Split(',');
        Assert.Equal(ScanExportService.Columns.Count, fields.Length);
    }

    [Fact]
    public void WriteRow_WritesAiCategoryWhenThereIsNoOverride()
    {
        var sw = new StringWriter();
        ScanExportService.WriteRow(sw, MakeClassifiedFile());

        var fields = sw.ToString().TrimEnd('\r', '\n').Split(',');
        Assert.Equal(nameof(FileCategory.Photo), fields[ColumnIndex("category")]);
        Assert.Equal("ai", fields[ColumnIndex("category_source")]);
    }

    [Fact]
    public void WriteRow_UserOverrideBeatsAiCategory()
    {
        var file = MakeClassifiedFile();
        file.Override = new UserOverride
        {
            FileNodeId = file.Id,
            OverriddenCategory = FileCategory.TrashCandidate,
            OverriddenTarget = "D:\\Trash"
        };

        var sw = new StringWriter();
        ScanExportService.WriteRow(sw, file);

        var fields = sw.ToString().TrimEnd('\r', '\n').Split(',');
        Assert.Equal(nameof(FileCategory.TrashCandidate), fields[ColumnIndex("category")]);
        Assert.Equal("user", fields[ColumnIndex("category_source")]);
        Assert.Equal("D:\\Trash", fields[ColumnIndex("suggested_target")]);
    }

    [Fact]
    public void WriteRow_UnclassifiedFileLeavesAiColumnsEmpty()
    {
        var file = MakeClassifiedFile();
        file.Classification = null;

        var sw = new StringWriter();
        ScanExportService.WriteRow(sw, file);

        var fields = sw.ToString().TrimEnd('\r', '\n').Split(',');
        Assert.Equal(string.Empty, fields[ColumnIndex("category")]);
        Assert.Equal(string.Empty, fields[ColumnIndex("confidence")]);
        Assert.Equal(string.Empty, fields[ColumnIndex("category_source")]);
        // ... but the scan facts are still there.
        Assert.Equal("img001.jpg", fields[ColumnIndex("name")]);
    }

    [Fact]
    public void WriteRow_ConfidenceIsInvariantCulture()
    {
        // On a German machine "0,85" would add a phantom column.
        var sw = new StringWriter();
        ScanExportService.WriteRow(sw, MakeClassifiedFile());

        var fields = sw.ToString().TrimEnd('\r', '\n').Split(',');
        Assert.Equal("0.85", fields[ColumnIndex("confidence")]);
    }

    [Fact]
    public void WriteRow_SummaryWithCommaAndNewlineStaysOneRow()
    {
        var file = MakeClassifiedFile();
        file.Classification!.Summary = "Urlaub, Strand\nzweite Zeile";

        var sw = new StringWriter();
        ScanExportService.WriteRow(sw, file);

        var text = sw.ToString();
        Assert.EndsWith("\r\n", text);
        // The only unquoted line break is the record separator at the very end.
        Assert.Contains("\"Urlaub, Strand\nzweite Zeile\"", text);
    }

    // ------------------------------------------------------------------ full job

    [Fact]
    public async Task ExportJobAsync_WritesHeaderPlusOneRowPerScannedFile()
    {
        var job = await _jobRepo.CreateAsync(new ScanJob
        {
            RootPath = "/Scan",
            Status = JobStatus.Created,
            CreatedAt = DateTime.UtcNow
        });

        var fakeFs = new FakeFileSystemAccessor(new Dictionary<string, string[]>
        {
            ["/Scan"] = ["/Scan/doc.pdf", "/Scan/photo.jpg"],
            ["/Scan/Videos"] = ["/Scan/Videos/clip.mp4"],
            ["/Scan/Videos/Sub"] = ["/Scan/Videos/Sub/nested.txt"],
        });

        var engine = new ScanEngine(
            fakeFs,
            new HeuristicsEngine(new HeuristicsOptions()),
            _fileRepo, _dirRepo, _jobRepo,
            NullLogger<ScanEngine>.Instance);

        await engine.RunAsync(job, new Progress<ScanProgress>(_ => { }), CancellationToken.None);

        var sut = new ScanExportService(_dirRepo, _fileRepo);
        var sw = new StringWriter();
        var rows = await sut.ExportJobAsync(job.Id, sw);

        Assert.Equal(4, rows);

        var lines = sw.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length); // header + 4 files
        Assert.Equal(string.Join(',', ScanExportService.Columns), lines[0]);
        Assert.Contains(lines, l => l.StartsWith("/Scan/Videos/Sub/nested.txt,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExportJobAsync_UnknownJob_WritesHeaderOnly()
    {
        var sut = new ScanExportService(_dirRepo, _fileRepo);
        var sw = new StringWriter();

        var rows = await sut.ExportJobAsync(987654, sw);

        Assert.Equal(0, rows);
        Assert.Equal(string.Join(',', ScanExportService.Columns) + "\r\n", sw.ToString());
    }

    [Fact]
    public async Task ExportJobToFileAsync_WritesUtf8WithBom()
    {
        var job = await _jobRepo.CreateAsync(new ScanJob
        {
            RootPath = "/Scan",
            Status = JobStatus.Created,
            CreatedAt = DateTime.UtcNow
        });

        var target = Path.Combine(Path.GetTempPath(), $"sfo_export_{Guid.NewGuid():N}.csv");
        try
        {
            var sut = new ScanExportService(_dirRepo, _fileRepo);
            await sut.ExportJobToFileAsync(job.Id, target);

            var bytes = await File.ReadAllBytesAsync(target);
            Assert.True(bytes.Length >= 3);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        }
        finally
        {
            if (File.Exists(target)) File.Delete(target);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static int ColumnIndex(string name)
    {
        var idx = ScanExportService.Columns.ToList().IndexOf(name);
        Assert.True(idx >= 0, $"unknown column '{name}'");
        return idx;
    }

    private static FileNode MakeClassifiedFile() => new()
    {
        Id = 1,
        JobId = 1,
        FullPath = "/Scan/Photos/img001.jpg",
        Name = "img001.jpg",
        ParentPath = "/Scan/Photos",
        RelativeDir = "Photos",
        Extension = ".jpg",
        FileType = FileType.Image,
        Status = FileNodeStatus.Analyzed,
        Size = 2048,
        LastWriteTime = new DateTime(2026, 9, 17, 10, 30, 0, DateTimeKind.Utc),
        Classification = new FileClassification
        {
            FileNodeId = 1,
            Category = FileCategory.Photo,
            Importance = Importance.Unknown,
            Confidence = 0.85,
            Summary = "Strandfoto",
            SuggestedTarget = "D:\\Fotos\\2026",
            ModelUsed = "gemma4:26b",
            AnalyzedAt = new DateTime(2026, 9, 17, 11, 0, 0, DateTimeKind.Utc),
            IsAiResult = true
        }
    };
}
