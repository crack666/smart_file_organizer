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

    private static (OllamaService Service, Capturing Handler) Service(Func<int, string>? answer = null)
    {
        var handler = new Capturing(answer ?? (_ => Chat(Answer)));
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return (new OllamaService(http, new OllamaOptions { BaseUrl = "http://model.test", MaxRetries = 0 },
            NullLogger<OllamaService>.Instance), handler);
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
