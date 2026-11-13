using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Stage 1 of https://github.com/crack666/smart_file_organizer/issues/37: the classification call also describes what
/// a picture shows and what a document is, as facts with source model, without a longer fixed part and without
/// describing a marked file.
/// </summary>
public class DescriptionTests
{
    private sealed class Capturing(string content) : HttpMessageHandler
    {
        public List<JsonDocument> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Chat(content), Encoding.UTF8, "application/json")
            };
        }
    }

    private static string Chat(string content) =>
        JsonSerializer.Serialize(new { message = new { role = "assistant", content }, done = true, done_reason = "stop" });

    private static (OllamaService Service, Capturing Handler) Service(string answer)
    {
        var handler = new Capturing(answer);
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return (new OllamaService(http, new OllamaOptions { BaseUrl = "http://model.test", MaxRetries = 0 },
            NullLogger<OllamaService>.Instance), handler);
    }

    private static FileNode File(string name, FileType type, SensitiveMarker markers = SensitiveMarker.None) => new()
    {
        Id = name.Length, Name = name, Extension = Path.GetExtension(name), FileType = type, Size = 1234,
        LastWriteTime = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), RelativeDir = "x",
        SensitiveMarkers = markers, SensitiveRule = markers == SensitiveMarker.None ? null : "test"
    };

    private static List<string> Required(JsonDocument request) =>
        request.RootElement.GetProperty("format").GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToList();

    private const string PictureAnswer = """
        {"category":"Photo","importance":"High","confidence":0.9,"summary":"Two people skiing.","sensitive":[],
         "scene":"ski slope with snow","activity":"skiing","setting":"outdoor","season":"winter","occasion":"holiday","people":true}
        """;

    [Fact]
    public async Task Pictures_and_documents_are_described_with_a_few_codes_the_rest_and_marked_files_not()
    {
        var (service, handler) = Service(PictureAnswer);
        await service.ClassifyAsync(new OllamaClassificationInput { File = File("ski.jpg", FileType.Image), Base64Images = ["aW1n"] });
        await service.ClassifyAsync(new OllamaClassificationInput { File = File("bill.pdf", FileType.Document), ExtractedText = "Invoice" });
        await service.ClassifyAsync(new OllamaClassificationInput { File = File("tax.pdf", FileType.Document, SensitiveMarker.Finance) });
        await service.ClassifyAsync(new OllamaClassificationInput { File = File("main.py", FileType.Code), ExtractedText = "print(1)" });
        await service.ClassifyAsync(new OllamaClassificationInput { File = File("raw.heic", FileType.Image) });

        string[] picture = ["scene", "activity", "setting", "season", "occasion", "people"];
        string[] document = ["document_kind", "issuer", "year"];
        Assert.Superset(picture.ToHashSet(), Required(handler.Requests[0]).ToHashSet());
        Assert.DoesNotContain("document_kind", Required(handler.Requests[0]));
        Assert.Superset(document.ToHashSet(), Required(handler.Requests[1]).ToHashSet());
        Assert.DoesNotContain("scene", Required(handler.Requests[1]));
        foreach (var plain in handler.Requests.Skip(2))
            Assert.Empty(Required(plain).Intersect(picture.Concat(document)));

        // The fixed part stays one text for every file: the description is asked through the schema only.
        var systems = handler.Requests.Select(r => r.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()).Distinct().ToList();
        Assert.Single(systems);
        Assert.Contains("document_kind", systems[0]);
    }

    [Fact]
    public async Task The_answer_becomes_facts_with_source_model()
    {
        var (service, _) = Service(PictureAnswer);
        var result = await service.ClassifyAsync(new OllamaClassificationInput { File = File("ski.jpg", FileType.Image), Base64Images = ["aW1n"] });

        Assert.Equal(
        [
            new FileFact(FileFactKey.Scene, "ski slope with snow", FactSource.Model),
            new FileFact(FileFactKey.Activity, "skiing", FactSource.Model),
            new FileFact(FileFactKey.Setting, "outdoor", FactSource.Model),
            new FileFact(FileFactKey.Season, "winter", FactSource.Model),
            new FileFact(FileFactKey.Occasion, "holiday", FactSource.Model),
            new FileFact(FileFactKey.People, "true", FactSource.Model)
        ], result.DescribedFacts);
    }

    [Fact]
    public void Unknown_empty_and_impossible_values_are_no_statement()
    {
        var result = new FileClassification();
        OllamaService.ParseResponse(Chat("""
            {"category":"Invoice","importance":"Medium","confidence":0.9,"summary":"s","sensitive":[],
             "document_kind":"Invoice","issuer":"  Stadtwerke Musterstadt GmbH und Co. KG, Abteilung Abrechnung und Forderungsmanagement  ","year":2019,
             "scene":"unknown","activity":"none","setting":"unknown","season":"spring time","occasion":"birthday","people":"yes"}
            """), result);

        Assert.Equal(3, result.DescribedFacts.Count);
        Assert.Contains(new FileFact(FileFactKey.DocumentKind, "invoice", FactSource.Model), result.DescribedFacts);
        Assert.Contains(new FileFact(FileFactKey.Year, "2019", FactSource.Model), result.DescribedFacts);
        var issuer = Assert.Single(result.DescribedFacts, f => f.Key == FileFactKey.Issuer);
        Assert.True(issuer.Value.Length <= OllamaService.IssuerMaxLength);
        Assert.StartsWith("Stadtwerke", issuer.Value);

        var none = new FileClassification();
        OllamaService.ParseResponse(Chat("""{"category":"Document","confidence":0.9,"summary":"s","sensitive":[],"document_kind":"unknown","issuer":"","year":0}"""), none);
        Assert.Empty(none.DescribedFacts);

        // Answers stored before the description existed stay readable and describe nothing.
        var older = new FileClassification();
        OllamaService.ParseResponse(Chat("""{"category":"Photo","importance":"High","confidence":0.8,"summary":"s","suggested_target":"Photos/2020","sensitive":[]}"""), older);
        Assert.Equal(FileCategory.Photo, older.Category);
        Assert.Empty(older.DescribedFacts);
    }

    [Fact]
    public void A_marked_file_keeps_no_description_whoever_marked_it()
    {
        IReadOnlyList<FileFact> read = [new FileFact(FileFactKey.PageCount, "2"), new FileFact(FileFactKey.Title, "Steuer 2019")];
        FileClassification Answer(SensitiveMarker markers) => new()
        {
            Category = FileCategory.PersonalDocument, SensitiveMarkers = markers,
            DescribedFacts =
            [
                new FileFact(FileFactKey.DocumentKind, "tax", FactSource.Model),
                new FileFact(FileFactKey.Issuer, "Finanzamt", FactSource.Model),
                new FileFact(FileFactKey.Year, "2019", FactSource.Model)
            ]
        };

        var plain = ClassificationService.FactsToStore(File("a.pdf", FileType.Document), Answer(SensitiveMarker.None), read);
        Assert.Equal(5, plain.Count);
        Assert.Contains(new FileFact(FileFactKey.Issuer, "Finanzamt", FactSource.Model), plain);

        var byModel = ClassificationService.FactsToStore(File("a.pdf", FileType.Document), Answer(SensitiveMarker.Finance), read);
        Assert.Equal([new FileFact(FileFactKey.PageCount, "2")], byModel);

        var byRule = ClassificationService.FactsToStore(File("a.pdf", FileType.Document, SensitiveMarker.Finance), Answer(SensitiveMarker.None), read);
        Assert.Equal([new FileFact(FileFactKey.PageCount, "2")], byRule);

        // A failed call describes nothing.
        var failed = Answer(SensitiveMarker.None);
        failed.Error = "timeout";
        Assert.Equal(read, ClassificationService.FactsToStore(File("a.pdf", FileType.Document), failed, read));
    }

    [Fact]
    public void The_prompt_fingerprint_covers_the_description()
    {
        var instructions = OllamaService.BuildClassificationInstructions(askKeepHint: false, "Deutsch");
        Assert.Contains("Write the summary, scene and activity fields in: Deutsch.", instructions);
        foreach (var code in VerdictCodes.All<OccasionKind>()) Assert.Contains(code, instructions);
        // Short answers stay short: no sentence fields beyond the summary, and the texts have a limit.
        using var schema = JsonDocument.Parse(JsonSerializer.Serialize(OllamaService.BuildClassificationSchema(false, DescriptionKind.Picture)));
        Assert.Equal(OllamaService.SceneMaxLength, schema.RootElement.GetProperty("properties").GetProperty("scene").GetProperty("maxLength").GetInt32());
    }
}
