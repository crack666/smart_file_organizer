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
/// The shape of the call to the model: a fixed start that the server can take from its prompt cache,
/// a short answer, a cap on the answer length and the counts the server reports.
/// </summary>
public class ModelCallTests
{
    /// <summary>Answers every <c>/api/chat</c> call with a canned body and keeps the request bodies.</summary>
    private sealed class Capturing(Func<int, string> answer) : HttpMessageHandler
    {
        public List<JsonDocument> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer(Requests.Count), Encoding.UTF8, "application/json")
            };
        }
    }

    private static string Chat(string content, string doneReason = "stop") =>
        JsonSerializer.Serialize(new
        {
            message = new { role = "assistant", content },
            done = true,
            done_reason = doneReason,
            prompt_eval_count = 2000,
            prompt_eval_cached_count = 1500,
            eval_count = 40,
            prompt_eval_duration = 500_000_000L,
            eval_duration = 250_000_000L
        });

    private const string Answer = """{"category":"Document","importance":"Medium","confidence":0.9,"summary":"A note.","sensitive":[]}""";

    private static (OllamaService Service, Capturing Handler) Service(Func<int, string>? answer = null, ModelUsageMeter? meter = null)
    {
        var handler = new Capturing(answer ?? (_ => Chat(Answer)));
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return (new OllamaService(http, new OllamaOptions { BaseUrl = "http://model.test", MaxRetries = 0 },
            NullLogger<OllamaService>.Instance, meter: meter), handler);
    }

    private static OllamaClassificationInput Note() => new()
    {
        File = File("note.txt", ".txt", FileType.Document), ExtractedText = "shopping list"
    };

    [Fact]
    public async Task Every_phase_sets_its_answer_limit()
    {
        var (service, handler) = Service();
        await service.ClassifyAsync(Note());
        var options = new OllamaOptions();
        Assert.Equal(options.ClassificationMaxAnswerTokens,
            handler.Requests[0].RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.True(options.ClassificationMaxAnswerTokens >= 4 * 108);
        Assert.True(options.PreAssessmentMaxAnswerTokens > options.ClassificationMaxAnswerTokens);
        Assert.True(options.SummaryMaxAnswerTokens > options.ClassificationMaxAnswerTokens);
    }

    [Fact]
    public async Task A_cut_off_answer_is_asked_again_once()
    {
        var meter = new ModelUsageMeter();
        var (service, handler) = Service(n => n == 1 ? Chat("""{"category":"Docu""", "length") : Chat(Answer), meter);
        var result = await service.ClassifyAsync(Note());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(result.Error);
        Assert.Equal(FileCategory.Document, result.Category);
        var usage = Assert.Single(meter.Snapshot());
        Assert.Equal(2, usage.Calls);
        Assert.Equal(1, usage.CutOff);
    }

    [Fact]
    public async Task An_answer_cut_off_twice_is_an_error_not_a_partial_result()
    {
        var (service, handler) = Service(_ => Chat("""{"category":"Photo","importance":"Hi""", "length"));
        var result = await service.ClassifyAsync(Note());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("cut off", result.Error);
        Assert.NotEqual(FileCategory.Photo, result.Category);
    }

    [Fact]
    public async Task The_meter_counts_calls_and_tokens_per_kind()
    {
        var meter = new ModelUsageMeter();
        var (service, _) = Service(meter: meter);
        await service.ClassifyAsync(Note());
        await service.ClassifyAsync(Note());
        await service.ClassifyAsync(new OllamaClassificationInput
        {
            File = File("x.txt", ".txt", FileType.Document), Context = new ClassificationContext(null, null, null, [], 1)
        });

        var usage = meter.Snapshot();
        Assert.Equal([ModelCallKinds.Classification, ModelCallKinds.SecondCheck], usage.Select(u => u.Kind));
        var c = usage[0];
        Assert.Equal(2, c.Calls);
        Assert.Equal(4000, c.PromptTokens);
        Assert.Equal(3000, c.CachedPromptTokens);
        Assert.Equal(80, c.AnswerTokens);
        Assert.Equal(0.75, c.MeanSeconds, 3);
        Assert.Equal(0, c.CutOff);
    }

    /// <summary>Everything the model reads, in order: the text of all messages; images go where the server puts them, before the user text.</summary>
    private static string Rendered(JsonDocument request)
    {
        var sb = new StringBuilder();
        foreach (var m in request.RootElement.GetProperty("messages").EnumerateArray())
        {
            sb.Append('<').Append(m.GetProperty("role").GetString()).Append('>');
            if (m.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                foreach (var img in images.EnumerateArray()) sb.Append("[img:").Append(img.GetString()).Append(']');
            sb.Append(m.GetProperty("content").GetString());
        }
        return sb.ToString();
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return n;
    }

    private static FileNode File(string name, string ext, FileType type) => new()
    {
        Id = name.Length, Name = name, Extension = ext, FileType = type, Size = 1234,
        LastWriteTime = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc), RelativeDir = "docs"
    };

    [Fact]
    public async Task Classification_calls_for_different_files_share_the_whole_fixed_part()
    {
        var (service, handler) = Service();
        await service.ClassifyAsync(new OllamaClassificationInput
        {
            File = File("letter.pdf", ".pdf", FileType.Document), ExtractedText = "Dear landlord, I hereby cancel"
        });
        await service.ClassifyAsync(new OllamaClassificationInput
        {
            File = File("beach.jpg", ".jpg", FileType.Image), Base64Images = ["aW1hZ2U="],
            Context = new ClassificationContext("uniform", "holiday", "keep", [], 3)
        });
        await service.ClassifyAsync(new OllamaClassificationInput
        {
            File = File("setup.exe", ".exe", FileType.Executable), AskKeepHint = true
        });

        var options = new OllamaOptions();
        var fixedPart = "<system>" + options.SystemPrompt.TrimEnd() + "\n\n"
                        + OllamaService.BuildClassificationInstructions(askKeepHint: false, options.SummaryLanguage);
        var first = Rendered(handler.Requests[0]);
        Assert.StartsWith(fixedPart, first);
        Assert.True(CommonPrefix(first, Rendered(handler.Requests[1])) >= fixedPart.Length);
        // The keep hint block comes after the common part, so a call that asks for it shares all of it too.
        Assert.True(CommonPrefix(first, Rendered(handler.Requests[2])) >= fixedPart.Length);

        // Nothing about one file in the fixed part, none of the guidance in the part about the file.
        Assert.DoesNotContain("letter.pdf", fixedPart);
        var user = handler.Requests[0].RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.DoesNotContain("Classification guidance", user);
        Assert.Contains("letter.pdf", user);
    }

    [Fact]
    public void The_answer_asks_no_target_and_a_keep_reason_only_for_temporary()
    {
        foreach (var askKeepHint in new[] { false, true })
        {
            using var schema = JsonDocument.Parse(JsonSerializer.Serialize(OllamaService.BuildClassificationSchema(askKeepHint)));
            var properties = schema.RootElement.GetProperty("properties");
            var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.False(properties.TryGetProperty("suggested_target", out _));
            Assert.DoesNotContain("suggested_target", required);
            Assert.DoesNotContain("keep_reason", required);
            Assert.Equal(OllamaService.SummaryMaxLength, properties.GetProperty("summary").GetProperty("maxLength").GetInt32());
            Assert.Equal(askKeepHint, properties.TryGetProperty("keep_reason", out _));
        }

        var instructions = OllamaService.BuildClassificationInstructions(askKeepHint: true);
        Assert.Contains("only for temporary also a short keep_reason", instructions);
        Assert.Contains("summary: one short sentence", instructions);
        Assert.DoesNotContain("suggested_target", instructions);
        Assert.DoesNotContain("keep_reason", OllamaService.BuildClassificationInstructions(askKeepHint: false));
    }

    [Fact]
    public void Stored_older_answers_with_target_and_reason_stay_readable()
    {
        var result = new FileClassification();
        OllamaService.ParseResponse(Chat("""{"category":"Photo","importance":"High","confidence":0.8,"summary":"s","suggested_target":"Photos/2020","sensitive":[],"keep_hint":"worth_keeping","keep_reason":"family"}"""), result);
        Assert.Equal("Photos/2020", result.SuggestedTarget);
        Assert.Equal(KeepHint.WorthKeeping, result.KeepHint);
        Assert.Equal("family", result.KeepReason);

        var newer = new FileClassification();
        OllamaService.ParseResponse(Chat("""{"category":"SoftwareInstaller","importance":"Low","confidence":0.9,"summary":"An installer.","sensitive":[],"keep_hint":"temporary","keep_reason":"setup package"}"""), newer);
        Assert.Null(newer.SuggestedTarget);
        Assert.Equal(KeepHint.Temporary, newer.KeepHint);
        Assert.Equal("setup package", newer.KeepReason);
    }

    [Fact]
    public async Task Directory_calls_share_their_fixed_task()
    {
        var (service, handler) = Service(_ => Chat("""{"verdict":"mixed","purpose":"p","retention":"review","reason":"r"}"""));
        DirectoryPreAssessmentInput Dir(string name, int files) => new()
        {
            Directory = new DirectoryNode { Id = files, Name = name, RelativePath = name },
            DirectFiles = Enumerable.Range(0, files).Select(i => new DirectoryFileInfo { Name = $"{name}{i}.txt", Size = i }).ToList(),
            SubdirectoryNames = []
        };
        await service.PreAssessDirectoryAsync(Dir("alpha", 2));
        await service.PreAssessDirectoryAsync(Dir("beta", 5));
        var fixedPhase1 = "<system>" + OllamaService.BuildPreAssessmentInstructions();
        Assert.StartsWith(fixedPhase1, Rendered(handler.Requests[0]));
        Assert.StartsWith(fixedPhase1, Rendered(handler.Requests[1]));

        DirectorySummaryInput Sum(string name) => new()
        {
            Directory = new DirectoryNode { Name = name, RelativePath = name },
            PreAssessment = new DirectoryClassificationResult(),
            FileSummaryLines = [$"{name}.txt | Document"]
        };
        await service.SummarizeDirectoryAsync(Sum("alpha"));
        await service.SummarizeDirectoryAsync(Sum("beta"));
        var a = Rendered(handler.Requests[2]);
        var b = Rendered(handler.Requests[3]);
        Assert.True(CommonPrefix(a, b) >= a.IndexOf("<user>", StringComparison.Ordinal));
    }
}
