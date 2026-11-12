using System.Text.Json;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Evaluation;

/// <summary>What a run ran with. Two runs are comparable when these are known.</summary>
/// <param name="Model">Model name, or the stub name.</param>
/// <param name="Stub">File name of the scripted answers, null for a real model.</param>
/// <param name="PromptFingerprint">Hash over system prompt, prompt templates and answer schemas.</param>
/// <param name="AppVersion">Version of the build, including the commit when the build knows it.</param>
/// <param name="Settings">Settings and thresholds as strings, for example <c>ollama.max_parallel_requests</c>.</param>
public sealed record EvalRunContext(
    string? Model,
    string? Stub,
    string? PromptFingerprint,
    string? AppVersion,
    IReadOnlyDictionary<string, string> Settings);

public sealed record EvalRunSummary(
    long Id,
    EvalRunMode Mode,
    DateTime StartedAt,
    DateTime FinishedAt,
    string Subject,
    long? JobId,
    string? Model,
    string? Stub,
    string? PromptFingerprint,
    string? AppVersion);

/// <summary>A difference in what two runs ran with.</summary>
public sealed record EvalSettingChange(string Name, string? Before, string? After);

public sealed record EvalScoreDelta(
    EvalKind Kind,
    int BeforeHits,
    int BeforeTotal,
    double? BeforeAccuracy,
    int AfterHits,
    int AfterTotal,
    double? AfterAccuracy,
    double? Change);

/// <summary>
/// Two runs side by side: what changed between them, the score per kind, and which items were
/// fixed or newly broken. Items are matched by <see cref="EvalFailure.Key"/>.
/// </summary>
/// <param name="SameSubject">False when the runs measured different corpora or modes; the item lists are then not meaningful.</param>
/// <param name="Fixed">Failed in the earlier run, not in the later one (details from the earlier run).</param>
/// <param name="NewFailures">Failed in the later run, not in the earlier one.</param>
public sealed record EvalComparison(
    EvalRunSummary Before,
    EvalRunSummary After,
    bool SameSubject,
    IReadOnlyList<EvalSettingChange> Changes,
    IReadOnlyList<EvalScoreDelta> Scores,
    IReadOnlyList<EvalFailure> Fixed,
    IReadOnlyList<EvalFailure> NewFailures,
    int StillFailing);

/// <summary>Stores evaluation runs and compares two of them.</summary>
public sealed class EvalRunService
{
    private readonly IEvalRunRepository _runs;

    public EvalRunService(IEvalRunRepository runs) => _runs = runs;

    public async Task<EvalRun> RecordAsync(
        EvalRunMode mode, string subject, long? jobId, EvalRunContext context, EvalReport report,
        DateTime startedAt, CancellationToken ct = default)
    {
        var run = new EvalRun
        {
            Mode = mode,
            StartedAt = startedAt,
            FinishedAt = DateTime.UtcNow,
            Subject = subject,
            JobId = jobId,
            Model = context.Model,
            Stub = context.Stub,
            PromptFingerprint = context.PromptFingerprint,
            AppVersion = context.AppVersion,
            SettingsJson = JsonSerializer.Serialize(new SortedDictionary<string, string>(
                context.Settings.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal), EvalJson.Compact),
            ReportJson = JsonSerializer.Serialize(report, EvalJson.Compact)
        };
        // Stored even if the caller is being cancelled: the measurement is done.
        await _runs.InsertAsync(run, CancellationToken.None);
        return run;
    }

    /// <summary>
    /// Compares two stored runs. With two ids: those two, the first as "before". With one id, or
    /// none (the newest run): that run against the newest earlier run with the same mode and subject.
    /// Returns null when there is no earlier run to compare with; throws
    /// <see cref="KeyNotFoundException"/> for an unknown id.
    /// </summary>
    public async Task<EvalComparison?> CompareAsync(long? firstId, long? secondId, CancellationToken ct = default)
    {
        if (firstId is { } b && secondId is { } a)
            return Compare(await RequireAsync(b, ct), await RequireAsync(a, ct));

        var after = firstId is { } only
            ? await RequireAsync(only, ct)
            : (await _runs.GetLatestAsync(null, 1, ct)).FirstOrDefault();
        if (after is null)
            return null;
        var before = (await _runs.GetLatestAsync(after.Mode, 1000, ct))
            .FirstOrDefault(r => r.Id < after.Id && r.Subject == after.Subject);
        return before is null ? null : Compare(before, after);
    }

    public static EvalReport ReadReport(EvalRun run) =>
        JsonSerializer.Deserialize<EvalReport>(run.ReportJson, EvalJson.Compact)
        ?? new EvalReport([], [], 0, []);

    public static EvalRunSummary Summarize(EvalRun r) =>
        new(r.Id, r.Mode, r.StartedAt, r.FinishedAt, r.Subject, r.JobId, r.Model, r.Stub, r.PromptFingerprint, r.AppVersion);

    public static EvalComparison Compare(EvalRun before, EvalRun after)
    {
        var reportBefore = ReadReport(before);
        var reportAfter = ReadReport(after);

        var changes = new List<EvalSettingChange>();
        void Diff(string name, string? x, string? y)
        {
            if (!string.Equals(x, y, StringComparison.Ordinal)) changes.Add(new EvalSettingChange(name, x, y));
        }
        Diff("model", before.Model, after.Model);
        Diff("stub", before.Stub, after.Stub);
        Diff("prompt_fingerprint", before.PromptFingerprint, after.PromptFingerprint);
        Diff("app_version", before.AppVersion, after.AppVersion);
        var settingsBefore = ReadSettings(before);
        var settingsAfter = ReadSettings(after);
        foreach (var name in settingsBefore.Keys.Union(settingsAfter.Keys).Order(StringComparer.Ordinal))
            Diff(name, settingsBefore.GetValueOrDefault(name), settingsAfter.GetValueOrDefault(name));

        var scores = reportBefore.Scores.Select(s => s.Kind).Union(reportAfter.Scores.Select(s => s.Kind))
            .Select(kind =>
            {
                var x = reportBefore.Scores.FirstOrDefault(s => s.Kind == kind);
                var y = reportAfter.Scores.FirstOrDefault(s => s.Kind == kind);
                double? change = x?.Accuracy is { } ax && y?.Accuracy is { } ay ? Math.Round(ay - ax, 4) : null;
                return new EvalScoreDelta(kind, x?.Hits ?? 0, x?.Total ?? 0, x?.Accuracy, y?.Hits ?? 0, y?.Total ?? 0, y?.Accuracy, change);
            }).ToList();

        var failedBefore = reportBefore.Failures.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var failedAfter = reportAfter.Failures.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var fixedItems = reportBefore.Failures.Where(f => !failedAfter.ContainsKey(f.Key)).ToList();
        var newItems = reportAfter.Failures.Where(f => !failedBefore.ContainsKey(f.Key)).ToList();
        var still = reportAfter.Failures.Count(f => failedBefore.ContainsKey(f.Key));

        return new EvalComparison(Summarize(before), Summarize(after),
            before.Mode == after.Mode && before.Subject == after.Subject,
            changes, scores, fixedItems, newItems, still);
    }

    private async Task<EvalRun> RequireAsync(long id, CancellationToken ct) =>
        await _runs.GetAsync(id, ct) ?? throw new KeyNotFoundException($"Evaluation run {id} not found.");

    private static Dictionary<string, string> ReadSettings(EvalRun run) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(run.SettingsJson) ?? [];
}
