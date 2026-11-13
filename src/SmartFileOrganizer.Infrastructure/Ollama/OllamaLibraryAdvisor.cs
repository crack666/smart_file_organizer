using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Ollama;

/// <summary>
/// Asks the configured local Ollama model about a library (<c>sfo library review</c>, <c>propose</c>, <c>show</c>
/// and <c>match</c>). Prompts and answer schemas for all questions live here, next to the other prompts. The facts hold
/// folder names, which can be private; they go to this server and nowhere else.
/// </summary>
public sealed class OllamaLibraryAdvisor : ILibraryAdvisor
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaLibraryAdvisor> _logger;

    public OllamaLibraryAdvisor(HttpClient http, OllamaOptions options, ILogger<OllamaLibraryAdvisor> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<LibraryAdviceReply> AskAsync(LibraryAdviceRequest request, CancellationToken ct = default)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? _options.Model : request.Model;
        var language = request.Language ?? _options.SummaryLanguage;
        if (request.Task == LibraryAdviceTask.Fit)
            return await SendAsync(request.Task, FitBody(model, request.Facts, language), model, ct);
        var prompt = request.Task switch
        {
            LibraryAdviceTask.Review => BuildReviewPrompt(request.Facts, language),
            LibraryAdviceTask.Map => BuildMapPrompt(request.Facts, language),
            LibraryAdviceTask.Name => BuildNamePrompt(request.Facts, language),
            _ => BuildProposePrompt(request.Facts, language)
        };
        var body = new
        {
            model,
            stream = false,
            think = false,
            keep_alive = _options.KeepAlive,
            format = request.Task switch
            {
                LibraryAdviceTask.Review => ReviewSchema(),
                LibraryAdviceTask.Map => MapSchema(),
                LibraryAdviceTask.Name => NameSchema(),
                _ => ProposeSchema()
            },
            messages = new object[] { new { role = "user", content = prompt } }
        };
        return await SendAsync(request.Task, body, model, ct);
    }

    private async Task<LibraryAdviceReply> SendAsync(LibraryAdviceTask task, object body, string model, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // One call over a whole tree: allow three times the per-file timeout.
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds * 3));
            var response = await _http.PostAsJsonAsync(new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), "api/chat"), body, cts.Token);
            var raw = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode)
                return new LibraryAdviceReply(null, model, raw, $"HTTP {(int)response.StatusCode} from Ollama.");

            using var doc = JsonDocument.Parse(raw);
            var content = doc.RootElement.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var c)
                ? c.GetString()
                : doc.RootElement.TryGetProperty("response", out var r) ? r.GetString() : null;
            _logger.LogInformation("[Library] {Task} answered with {Chars} characters", task, content?.Length ?? 0);
            return string.IsNullOrWhiteSpace(content)
                ? new LibraryAdviceReply(null, model, raw, "The model returned no content.")
                : new LibraryAdviceReply(content, model, raw, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new LibraryAdviceReply(null, model, null, $"Ollama request timed out after {_options.TimeoutSeconds * 3}s.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "[Library] {Task} failed", task);
            return new LibraryAdviceReply(null, model, null, ex.Message);
        }
    }

    public static string BuildReviewPrompt(string facts, string language)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You review the folder structure of a person's own archive (a home NAS or a folder). You see only folder names,");
        sb.AppendLine("counts, file types and years, plus findings of deterministic rules. You never see file contents.");
        sb.AppendLine();
        sb.AppendLine("Principles:");
        sb.AppendLine("- Continue the person's habits instead of replacing them. If photo folders are named by occasion (\"Ski trip 2015\"), keep that; never switch to folders by year.");
        sb.AppendLine("- Say explicitly what is already good; it stays as it is.");
        sb.AppendLine("- Decide the role of folders first: on one level, occasions, persons, devices and topics may lie side by side. Only siblings with the same role are comparable.");
        sb.AppendLine("- A number at the end of a name can be a year or an age; judge from the siblings.");
        sb.AppendLine("- A majority of names is a hint, not a decision. Prefer the most common pattern among the dated names for occasions.");
        sb.AppendLine("- Areas of life (home, car, health, work) carry a document structure better than persons.");
        sb.AppendLine("- Dumps (device or computer copies) are handled as a whole; do not restructure their insides.");
        sb.AppendLine("- Propose few changes with clear effect, each with a reason. Changes are suggestions; nothing is moved now.");
        sb.AppendLine();
        sb.AppendLine("Answer with JSON only:");
        sb.AppendLine("- good: folders that are fine as they are (path, reason).");
        sb.AppendLine($"- roles: role of important folders and of their children (path, role, children_role, reason). Roles: {string.Join(", ", VerdictCodes.All<FolderRole>())}.");
        sb.AppendLine($"- changes: (action, path, target, reason, finding). Actions: {string.Join(", ", VerdictCodes.All<ProposalAction>())}. "
                      + "path is an existing folder exactly as listed; target is the new name or path; finding is the finding code it answers, if any.");
        sb.AppendLine("- outline: the target structure in at most three levels (path, purpose, pattern for the names of the folders inside, if any).");
        sb.AppendLine($"Write reasons and purposes in {language}. Keep folder names in their own language.");
        sb.AppendLine();
        sb.AppendLine("Facts:");
        sb.Append(facts);
        return sb.ToString();
    }

    public static string BuildProposePrompt(string facts, string language)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A person has no ordered archive yet. From what was found on a source (folder names, counts, file types, dates,");
        sb.AppendLine("and where available a verdict on each folder), propose a target structure for a new archive.");
        sb.AppendLine();
        sb.AppendLine("Principles:");
        sb.AppendLine("- Few targets, at most two levels each. Only targets for content that was actually found.");
        sb.AppendLine("- Photos and videos of an occasion go together, named by occasion and year (pattern like \"{occasion} {year}\"), not by year alone.");
        sb.AppendLine("- Documents by area of life, not by person.");
        sb.AppendLine("- Programs, projects and device copies are kept whole.");
        sb.AppendLine("- Use the language of the source's folder names for target names.");
        sb.AppendLine();
        sb.AppendLine("Answer with JSON only: targets, each with id (lower case, no spaces), path (relative, '/' as separator), pattern (or empty),");
        sb.AppendLine("holds (file types: image, video, audio, document, archive, code, executable, database, font, other), reason, examples (folder names from the facts that would go there).");
        sb.AppendLine($"Write reasons in {language}.");
        sb.AppendLine();
        sb.AppendLine("Facts:");
        sb.Append(facts);
        return sb.ToString();
    }

    /// <summary><c>sfo library show</c>: one sentence per folder on what it is for, from names, counts and patterns.</summary>
    public static string BuildMapPrompt(string facts, string language)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You describe the folders of a person's own ordered archive, so that new things can be filed where they belong.");
        sb.AppendLine("You see only folder names, a role found by rules, counts, file types, periods and how subfolders are named.");
        sb.AppendLine("You never see file names or contents.");
        sb.AppendLine();
        sb.AppendLine("For each folder, say in one short sentence what it is for, the way the person would put it");
        sb.AppendLine("(\"Photos of trips and parties, one folder per occasion with its year\", \"Invoices by year\").");
        sb.AppendLine("Do not judge the order and do not propose changes. Do not invent content the facts do not show.");
        sb.AppendLine($"Write the purposes in {language}. Keep folder names exactly as listed.");
        sb.AppendLine();
        sb.AppendLine("Answer with JSON only: purposes, each with path (exactly as listed) and purpose.");
        sb.AppendLine();
        sb.AppendLine("Facts:");
        sb.Append(facts);
        return sb.ToString();
    }

    /// <summary><c>sfo library match</c>: the name of one new folder in the style of its siblings.</summary>
    public static string BuildNamePrompt(string facts, string language)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name one new folder in a person's own archive, in the style of the folders next to it.");
        sb.AppendLine("You see the naming pattern there, the sibling names, what goes in (counts and types), when it was taken,");
        sb.AppendLine("and the names of the folders it came from. You never see file names or contents.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Follow the pattern and the language of the sibling names; fill every placeholder.");
        sb.AppendLine("- Use the year or date of the files where the pattern has one.");
        sb.AppendLine("- Take the occasion from the names of the folders it came from when they say something; otherwise use a plain, neutral word");
        sb.AppendLine("  for the season or month (\"Sommer\", \"Juli\"), never invent an event, a place or a person.");
        sb.AppendLine("- One folder name only: no slashes, no placeholders, not the name of an existing sibling.");
        sb.AppendLine($"Write the reason in {language}.");
        sb.AppendLine();
        sb.AppendLine("Answer with JSON only: name, reason.");
        sb.AppendLine();
        sb.AppendLine("Facts:");
        sb.Append(facts);
        return sb.ToString();
    }

    /// <summary>Answer tokens for a content check: a code and one short sentence per candidate.</summary>
    public const int FitMaxAnswerTokens = 400;

    /// <summary>
    /// <c>sfo library match</c>, task <c>fit</c> (https://github.com/crack666/smart_file_organizer/issues/56): whether what
    /// a group of files shows fits each candidate folder. The fixed task is the system message, the same text for every
    /// call, so the server can keep it in its prompt cache; the descriptions are the user message. The answer is capped.
    /// </summary>
    public static string BuildFitSystemPrompt(string language)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You decide where files belong in a person's own archive. You get a description of a group of files (what pictures");
        sb.AppendLine("show, or kind and issuer of documents, with how many files show each value) and, per candidate folder, its name,");
        sb.AppendLine("its period and the same kind of description made from a few of its files. You never see the files themselves.");
        sb.AppendLine();
        sb.AppendLine("For each candidate answer one code:");
        sb.AppendLine("- fits: the same occasion or activity (the same trip, the same party), or the same kind of document from the same issuer.");
        sb.AppendLine("- contradicts: clearly something else: another occasion or activity, another kind of document or another issuer.");
        sb.AppendLine("  A matching period or year never makes a fit: a birthday in the week of a ski trip contradicts the ski trip folder.");
        sb.AppendLine("- unclear: the descriptions are too thin or too mixed to tell.");
        sb.AppendLine("Judge by what most files show; one odd picture does not change it. Do not invent what the descriptions do not say.");
        sb.AppendLine($"Give a reason of one short sentence in {language} that names what was seen on both sides.");
        sb.AppendLine("Answer with JSON only: verdicts, each with id (as listed), fit, reason.");
        return sb.ToString();
    }

    private object FitBody(string model, string facts, string language) => new
    {
        model,
        stream = false,
        think = false,
        keep_alive = _options.KeepAlive,
        format = FitSchema(),
        options = new { num_predict = FitMaxAnswerTokens },
        messages = new object[]
        {
            new { role = "system", content = BuildFitSystemPrompt(language) },
            new { role = "user", content = facts }
        }
    };

    private static object FitSchema() => new
    {
        type = "object",
        properties = new
        {
            verdicts = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        id = Str(),
                        fit = new { type = "string", @enum = VerdictCodes.All<ContentFit>() },
                        reason = Str()
                    },
                    required = new[] { "id", "fit", "reason" }
                }
            }
        },
        required = new[] { "verdicts" }
    };

    private static object MapSchema() => new
    {
        type = "object",
        properties = new
        {
            purposes = new
            {
                type = "array",
                items = new { type = "object", properties = new { path = Str(), purpose = Str() }, required = new[] { "path", "purpose" } }
            }
        },
        required = new[] { "purposes" }
    };

    private static object NameSchema() => new
    {
        type = "object",
        properties = new { name = Str(), reason = Str() },
        required = new[] { "name", "reason" }
    };

    private static object Str() => new { type = "string" };

    private static object ReviewSchema() => new
    {
        type = "object",
        properties = new
        {
            good = new { type = "array", items = new { type = "object", properties = new { path = Str(), reason = Str() }, required = new[] { "path", "reason" } } },
            roles = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        path = Str(),
                        role = new { type = "string", @enum = VerdictCodes.All<FolderRole>() },
                        children_role = new { type = "string", @enum = VerdictCodes.All<FolderRole>() },
                        reason = Str()
                    },
                    required = new[] { "path", "role" }
                }
            },
            changes = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { type = "string", @enum = VerdictCodes.All<ProposalAction>() },
                        path = Str(),
                        target = Str(),
                        reason = Str(),
                        finding = Str()
                    },
                    required = new[] { "action", "path", "reason" }
                }
            },
            outline = new { type = "array", items = new { type = "object", properties = new { path = Str(), purpose = Str(), pattern = Str() }, required = new[] { "path", "purpose" } } }
        },
        required = new[] { "good", "roles", "changes", "outline" }
    };

    private static object ProposeSchema() => new
    {
        type = "object",
        properties = new
        {
            targets = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        id = Str(),
                        path = Str(),
                        pattern = Str(),
                        holds = new { type = "array", items = Str() },
                        reason = Str(),
                        examples = new { type = "array", items = Str() }
                    },
                    required = new[] { "id", "path", "holds", "reason" }
                }
            }
        },
        required = new[] { "targets" }
    };
}
