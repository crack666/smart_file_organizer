using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Evaluation;

/// <summary>
/// Compares what a job stored (directory verdicts, model categories, sensitive markers) with the
/// truth of the corpus it scanned. Reads only; the job must be scanned and analysed before.
/// </summary>
/// <remarks>
/// What counts:
/// <list type="bullet">
/// <item><c>directory_verdict</c>: every truth folder with a verdict; the stored verdict of
/// <c>directory_verdicts</c>, whether a rule or the model decided.</item>
/// <item><c>file_category</c>: every truth file with a category; the category the model gave in
/// <c>ai_results</c>. A human override is ignored, because the model is measured.</item>
/// <item><c>sensitive</c>: every truth file; the markers of the rules and of the model together,
/// because together they decide whether a file is protected. A hit needs exactly the expected set.</item>
/// </list>
/// An item without a result (not scanned, not analysed, model error) is <see cref="EvalOutcome.Missing"/>.
/// </remarks>
public sealed class CorpusEvaluator
{
    public static readonly IReadOnlyList<EvalKind> Kinds = [EvalKind.DirectoryVerdict, EvalKind.FileCategory, EvalKind.Sensitive];

    private readonly IDirectoryRepository _directories;
    private readonly IFileRepository _files;
    private readonly IDirectoryVerdictRepository _verdicts;

    public CorpusEvaluator(IDirectoryRepository directories, IFileRepository files, IDirectoryVerdictRepository verdicts)
    {
        _directories = directories;
        _files = files;
        _verdicts = verdicts;
    }

    public async Task<EvalReport> EvaluateAsync(EvalTruth truth, long jobId, CancellationToken ct = default)
    {
        var report = new EvalReportBuilder();

        var dirs = await _directories.GetAllForJobOrderedByDepthAsync(jobId, ct);
        var verdicts = (await _verdicts.GetForJobAsync(jobId, ct)).ToDictionary(v => v.DirectoryNodeId);
        var dirByPath = new Dictionary<string, DirectoryNode>(StringComparer.Ordinal);
        var fileByPath = new Dictionary<string, FileNode>(StringComparer.Ordinal);
        foreach (var dir in dirs)
        {
            dirByPath[EvalTruth.NormalizePath(dir.RelativePath)] = dir;
            foreach (var file in await _files.GetByDirectoryAsync(jobId, dir.FullPath, ct))
                fileByPath[EvalTruth.NormalizePath(file.RelativePath)] = file;
        }

        foreach (var path in dirByPath.Keys.Where(p => !truth.Directories.ContainsKey(p))
                     .Concat(fileByPath.Keys.Where(p => !truth.Files.ContainsKey(p)))
                     .Order(StringComparer.Ordinal))
            report.Untracked(path);

        foreach (var expected in truth.Directories.Values.Where(d => d.Verdict is not null).OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            dirByPath.TryGetValue(expected.Path, out var node);
            var verdict = node is null ? null : verdicts.GetValueOrDefault(node.Id);
            ScoreVerdict(report, expected, node, verdict);
        }

        foreach (var expected in truth.Files.Values.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            fileByPath.TryGetValue(expected.Path, out var node);
            if (expected.Category is { } category)
                ScoreCategory(report, expected.Path, category, node);
            ScoreSensitive(report, expected.Path, expected.Sensitive, node);
        }

        return report.Build(Kinds);
    }

    private static void ScoreVerdict(EvalReportBuilder report, TruthDirectory expected, DirectoryNode? node, DirectoryVerdict? verdict)
    {
        var key = Key(EvalKind.DirectoryVerdict, expected.Path);
        var want = EvalCodes.Verdict(expected.Verdict!.Value);
        if (node is null)
        {
            report.Fail(new(EvalKind.DirectoryVerdict, key, expected.Path, EvalOutcome.Missing, want, null, null,
                "folder not in the scan (inside a skipped folder, or missing on disk)"));
            return;
        }
        if (verdict?.Verdict is not { } got)
        {
            report.Fail(new(EvalKind.DirectoryVerdict, key, expected.Path, EvalOutcome.Missing, want, null,
                verdict is null ? null : DecidedBy(verdict),
                verdict?.Error ?? "no verdict stored (pre-assessment not run for this job?)"));
            return;
        }
        if (got == expected.Verdict)
        {
            report.Hit(EvalKind.DirectoryVerdict);
            return;
        }
        if (verdict.EscalatedFrom is { } from)
        {
            // The pre-assessment said something else; the sample check of the staged analysis overruled it.
            report.Fail(new(EvalKind.DirectoryVerdict, key, expected.Path, EvalOutcome.Wrong, want, EvalCodes.Verdict(got),
                "sample", $"raised from {EvalCodes.Verdict(from)} ({DecidedBy(verdict)}): {verdict.EscalationReason}"));
            return;
        }
        report.Fail(new(EvalKind.DirectoryVerdict, key, expected.Path, EvalOutcome.Wrong, want, EvalCodes.Verdict(got),
            DecidedBy(verdict), verdict.Reason));
    }

    private static void ScoreCategory(EvalReportBuilder report, string path, FileCategory expected, FileNode? node)
    {
        var key = Key(EvalKind.FileCategory, path);
        var want = EvalCodes.Category(expected);
        var cls = node?.Classification;
        // An inherited result (taken over from the confirmed sample of its directory) or one a rule set counts like
        // a model answer: it is what the user gets for that file.
        var inherited = cls?.Origin == ResultOrigin.Inherited;
        var byRule = cls?.Origin == ResultOrigin.Rule;
        if (node is null || cls is null || (!cls.IsAiResult && !inherited && !byRule) || cls.Error is not null)
        {
            var why = node is null ? "file not in the scan"
                : cls?.Error is { } error ? $"model error: {error}"
                : $"not classified (file status {node.Status.ToString().ToLowerInvariant()})";
            report.Fail(new(EvalKind.FileCategory, key, path, EvalOutcome.Missing, want, null,
                cls?.ModelUsed is { } m ? $"model {m}" : null, why));
            return;
        }
        if (cls.Category == expected)
        {
            report.Hit(EvalKind.FileCategory);
            return;
        }
        report.Fail(new(EvalKind.FileCategory, key, path, EvalOutcome.Wrong, want, EvalCodes.Category(cls.Category),
            inherited ? "sample" : byRule ? "rule" : $"model {cls.ModelUsed}",
            inherited ? "inherited from the confirmed sample of its directory" : byRule ? cls.Summary : null));
    }

    private static void ScoreSensitive(EvalReportBuilder report, string path, SensitiveMarker expected, FileNode? node)
    {
        var key = Key(EvalKind.Sensitive, path);
        var byRule = node?.SensitiveMarkers ?? SensitiveMarker.None;
        var byModel = node?.Classification is { IsAiResult: true, Error: null } cls ? cls.SensitiveMarkers : SensitiveMarker.None;
        var actual = byRule | byModel;
        report.Sensitive(expected, actual);

        if (actual == expected)
        {
            report.Hit(EvalKind.Sensitive);
            return;
        }

        var missed = expected & ~actual;
        var extra = actual & ~expected;
        var parts = new List<string>();
        if (missed != SensitiveMarker.None) parts.Add("missed " + EvalCodes.MarkerList(missed));
        if (extra != SensitiveMarker.None) parts.Add("extra " + EvalCodes.MarkerList(extra));
        if (node is null) parts.Add("file not in the scan");

        var decidedBy = new List<string>();
        if (byRule != SensitiveMarker.None) decidedBy.Add($"rule {node!.SensitiveRule}");
        if (byModel != SensitiveMarker.None) decidedBy.Add($"model {node!.Classification!.ModelUsed}");

        report.Fail(new(EvalKind.Sensitive, key, path,
            node is null && expected != SensitiveMarker.None ? EvalOutcome.Missing : EvalOutcome.Wrong,
            EvalCodes.MarkerList(expected), EvalCodes.MarkerList(actual),
            decidedBy.Count == 0 ? null : string.Join(" + ", decidedBy),
            string.Join("; ", parts)));
    }

    private static string DecidedBy(DirectoryVerdict verdict) =>
        verdict.Source == VerdictSource.Rule
            ? $"rule {verdict.Rule}"
            : $"model {verdict.ModelUsed}";

    public static string Key(EvalKind kind, string path) => $"{kind.ToCode()}:{path}";
}
