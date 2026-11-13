using System.Globalization;
using System.Text;
using System.Text.Json;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Library;

/// <summary>A candidate folder as the content check sees it: name, period and the description of a sample of it.</summary>
/// <param name="Id">Short id in the question (<c>c1</c>, <c>c2</c>, ...).</param>
/// <param name="Key">Library job and relative path, to find the candidate again.</param>
public sealed record FitCandidate(string Id, string Key, MatchPlacement Placement, string RelativePath, string? Period, ContentDescription Description);

/// <summary>The judgement for one candidate, with one sentence why.</summary>
public sealed record FitVerdict(ContentFit Fit, string Reason);

/// <summary>The answer of a content check: per candidate id a verdict; <see cref="Status"/> says whether it ran.</summary>
public sealed record FitAnswer(ContentCheckStatus Status, IReadOnlyDictionary<string, FitVerdict> Verdicts, string? Model, string? Error)
{
    public static FitAnswer Without(ContentCheckStatus status, string? error = null) =>
        new(status, new Dictionary<string, FitVerdict>(), null, error);
}

/// <summary>
/// Compares what a unit of a source shows with what candidate folders show
/// (https://github.com/crack666/smart_file_organizer/issues/56). Sees descriptions only, never a file.
/// </summary>
public interface IContentFitJudge
{
    Task<FitAnswer> JudgeAsync(long sourceJobId, ContentDescription unit, IReadOnlyList<FitCandidate> candidates,
        string? model, bool refresh, CancellationToken ct = default);
}

/// <summary>
/// The local model judges (task <c>fit</c> of <see cref="ILibraryAdvisor"/>), cached in <c>library_advice</c> like the
/// other library questions. Candidates the answer leaves out, or answers with an unknown code, count as unclear.
/// </summary>
public sealed class ModelContentFitJudge : IContentFitJudge
{
    public const string FitTask = "fit";

    private readonly LibraryService _library;

    public ModelContentFitJudge(LibraryService library) => _library = library;

    public async Task<FitAnswer> JudgeAsync(long sourceJobId, ContentDescription unit, IReadOnlyList<FitCandidate> candidates,
        string? model, bool refresh, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return FitAnswer.Without(ContentCheckStatus.NoCandidates);
        var facts = Facts(unit, candidates);
        var (status, record) = await _library.AskAsync(sourceJobId, FitTask, LibraryAdviceTask.Fit, facts, model, refresh, ct);
        if (record is null) return FitAnswer.Without(status == AdviceStatus.Unavailable ? ContentCheckStatus.Unavailable : ContentCheckStatus.Failed);
        if (record.Error is not null) return new FitAnswer(ContentCheckStatus.Failed, new Dictionary<string, FitVerdict>(), record.Model, record.Error);
        var (verdicts, error) = Parse(record.Content, candidates.Select(c => c.Id).ToHashSet(StringComparer.Ordinal));
        return error is not null
            ? new FitAnswer(ContentCheckStatus.Failed, new Dictionary<string, FitVerdict>(), record.Model, error)
            : new FitAnswer(ContentCheckStatus.Checked, verdicts, record.Model, null);
    }

    /// <summary>What the model is told: the description of the files, then one line per candidate. No file names.</summary>
    public static string Facts(ContentDescription unit, IReadOnlyList<FitCandidate> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Files to place: {unit.Text()}");
        sb.AppendLine("Candidates (id | placement | folder | period | what a sample of it shows):");
        foreach (var c in candidates)
            sb.AppendLine($"{c.Id} | {c.Placement.ToCode()} | {c.RelativePath} | {c.Period ?? "-"} | {c.Description.Text()}");
        return sb.ToString();
    }

    public static (IReadOnlyDictionary<string, FitVerdict> Verdicts, string? Error) Parse(string? content, IReadOnlySet<string> ids)
    {
        var verdicts = new Dictionary<string, FitVerdict>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(content)) return (verdicts, "The model returned no content.");
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) return (verdicts, "The model's answer holds no JSON object.");
        try
        {
            using var doc = JsonDocument.Parse(content[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("verdicts", out var array) || array.ValueKind != JsonValueKind.Array)
                return (verdicts, "The model's answer has no verdicts.");
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = item.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()!.Trim() : null;
                var code = item.TryGetProperty("fit", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()!.Trim().ToLowerInvariant() : null;
                var reason = item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()!.Trim() : null;
                if (id is null || !ids.Contains(id) || !VerdictCodes.TryParse<ContentFit>(code, out var fit)) continue;
                if (string.IsNullOrWhiteSpace(reason)) reason = "no reason given";
                if (reason.Length > 300) reason = reason[..300].TrimEnd();
                verdicts.TryAdd(id, new FitVerdict(fit, reason));
            }
            return (verdicts, null);
        }
        catch (JsonException ex)
        {
            return (verdicts, $"The model's answer is not valid JSON: {ex.Message}");
        }
    }
}

/// <summary>
/// Judges by comparing the codes and words of two descriptions, without a model. Used by <c>sfo eval --stub</c>, where
/// the descriptions come from the truth, so the measurement checks the rest of the chain (sample, weighting, new folder)
/// and not a model. Not used by <c>sfo library match</c>: without a model the content counts as unchecked.
/// </summary>
public sealed class DescriptionRulesFitJudge : IContentFitJudge
{
    public const string Name = "description-rules";

    public Task<FitAnswer> JudgeAsync(long sourceJobId, ContentDescription unit, IReadOnlyList<FitCandidate> candidates,
        string? model, bool refresh, CancellationToken ct = default) =>
        Task.FromResult(candidates.Count == 0
            ? FitAnswer.Without(ContentCheckStatus.NoCandidates)
            : new FitAnswer(ContentCheckStatus.Checked,
                candidates.ToDictionary(c => c.Id, c => Judge(unit, c.Description), StringComparer.Ordinal), Name, null));

    public static FitVerdict Judge(ContentDescription unit, ContentDescription folder)
    {
        if (unit.IsEmpty || folder.IsEmpty)
            return new FitVerdict(ContentFit.Unclear, "One side is not described.");
        var u = unit.Gist();
        var f = folder.Gist();
        if (unit.IsDocuments != folder.IsDocuments)
            return new FitVerdict(ContentFit.Contradicts, $"The files are {(unit.IsDocuments ? "documents" : "pictures")} ({u}); the folder holds {(folder.IsDocuments ? "documents" : "pictures")} ({f}).");

        if (unit.IsDocuments)
        {
            var uk = unit.Top(FileFactKey.DocumentKind);
            var fk = folder.Top(FileFactKey.DocumentKind);
            if (uk is not null && fk is not null && !Same(uk, fk))
                return new FitVerdict(ContentFit.Contradicts, $"The files are {uk} ({u}); the folder holds {fk} ({f}).");
            var ui = unit.Top(FileFactKey.Issuer);
            var fi = folder.Top(FileFactKey.Issuer);
            if (ui is not null && fi is not null)
                return Same(ui, fi) || ui.Contains(fi, StringComparison.OrdinalIgnoreCase) || fi.Contains(ui, StringComparison.OrdinalIgnoreCase)
                    ? new FitVerdict(ContentFit.Fits, $"{u}; the folder holds the same: {f}.")
                    : new FitVerdict(ContentFit.Contradicts, $"The files come from {ui}; the folder holds papers of {fi}.");
            return new FitVerdict(ContentFit.Unclear, $"Both are {uk ?? fk ?? "documents"}, the issuer is not known on both sides.");
        }

        var uo = unit.Top(FileFactKey.Occasion);
        var fo = folder.Top(FileFactKey.Occasion);
        if (uo is not null && fo is not null && !Same(uo, fo))
            return new FitVerdict(ContentFit.Contradicts, $"The files show {u} ({uo}); the folder shows {f} ({fo}).");
        var shared = Words(unit).Intersect(Words(folder), StringComparer.OrdinalIgnoreCase).ToList();
        var us = unit.Top(FileFactKey.Season);
        var fs = folder.Top(FileFactKey.Season);
        if (shared.Count > 0 || uo is not null && us is not null && fs is not null && Same(us, fs))
            return new FitVerdict(ContentFit.Fits, $"{u}; the folder shows the same: {f}.");
        if (us is not null && fs is not null && !Same(us, fs))
            return new FitVerdict(ContentFit.Contradicts, $"The files show {us} ({u}); the folder shows {fs} ({f}).");
        return new FitVerdict(ContentFit.Unclear, $"The files show {u}; the folder shows {f}; nothing in common to tell.");
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Words of at least four letters of the scenes and activities.</summary>
    private static IEnumerable<string> Words(ContentDescription d) =>
        d.Of(FileFactKey.Scene).Concat(d.Of(FileFactKey.Activity))
            .SelectMany(v => v.Value.Split([' ', ',', '-', '/'], StringSplitOptions.RemoveEmptyEntries))
            .Select(w => w.ToLower(CultureInfo.InvariantCulture))
            .Where(w => w.Length >= 4)
            .Distinct();
}
