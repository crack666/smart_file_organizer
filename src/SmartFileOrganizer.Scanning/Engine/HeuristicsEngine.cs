using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Scanning.Rules;

namespace SmartFileOrganizer.Scanning.Engine;

/// <summary>What a decision rests on. Reported by <c>sfo rules check</c> as <c>basis</c>.</summary>
public enum DecisionBasis
{
    /// <summary>The scan root is always scanned.</summary>
    ScanRoot,
    /// <summary>A rule of the rule file matched.</summary>
    Rule,
    /// <summary>The safety depth limit was reached.</summary>
    SafetyLimit,
    /// <summary>A dot-folder that no rule covers.</summary>
    Hidden,
    /// <summary>No rule matched and the folder is above the shallow depth.</summary>
    NoRule,
    /// <summary>Beyond the shallow depth, the folder holds user files.</summary>
    UserContent,
    /// <summary>Beyond the shallow depth, the folder holds program files and no user files.</summary>
    ProgramContent,
    /// <summary>Beyond the shallow depth, the folder holds neither user nor program files.</summary>
    NoProgramContent,
    /// <summary>Beyond the shallow depth, the content could not be looked at.</summary>
    ContentUnknown
}

/// <summary>A decision with everything it rests on: what <see cref="DirectoryEvaluation"/> stores, plus
/// the rule and content counts for <c>sfo rules check</c>.</summary>
public sealed record RuleEvaluation(
    ScanDecision Decision,
    string Reason,
    DecisionBasis Basis,
    int Depth,
    ScanRule? Rule,
    int? RuleIndex,
    int? UserFiles,
    int? ProgramFiles,
    int? Files)
{
    public DirectoryEvaluation ToDirectoryEvaluation() => new(Decision, Reason);
}

/// <summary>Result of <see cref="HeuristicsEngine.Check"/>: the folder's own decision, and whether
/// the scan reaches it at all. If not, <see cref="BlockedAt"/> is the first folder above it the scan
/// does not enter, and <see cref="BlockedBy"/> that folder's decision.</summary>
public sealed record RuleCheck(
    string Path,
    string Root,
    RuleEvaluation Evaluation,
    bool Reached,
    string? BlockedAt,
    RuleEvaluation? BlockedBy);

/// <summary>
/// Decides per directory whether the scan enters it, records it shallow or skips it, from a
/// <see cref="ScanRuleSet"/>. Stateless; one instance per rule set.
/// </summary>
/// <remarks>
/// Order of precedence, first that applies wins:
/// <list type="number">
/// <item>The scan root is always scanned.</item>
/// <item>The rule matching closest to the folder (the deepest match; on a tie the rule listed first)
/// if it is a skip rule.</item>
/// <item>The safety limit: from that depth every folder is recorded shallow.</item>
/// <item>The deepest matching shallow or user-data rule.</item>
/// <item>Dot-folders are skipped (<see cref="ScanRuleSet.SkipHidden"/>).</item>
/// <item>Above <see cref="ScanRuleSet.ShallowFrom"/> the folder is scanned.</item>
/// <item>From there the content decides: user files → scan, program files only → shallow, anything else
/// → scan. The depth limit only applies to folders recognised as program folders, because a skipped
/// or flat folder of user data is the costliest mistake a scan can make.</item>
/// </list>
/// Skip and shallow rules match only below the scan root (what lies above it was chosen by the user);
/// user-data rules match the whole path, so a root inside <c>Documents</c> makes everything below it user data.
/// </remarks>
public class HeuristicsEngine : IHeuristicsEngine
{
    private readonly ScanRuleSet _rules;

    public HeuristicsEngine(ScanRuleSet rules) => _rules = rules;

    public ScanRuleSet Rules => _rules;

    public DirectoryEvaluation EvaluateDirectory(string rootPath, string fullPath, int depth, Func<DirectoryContent?>? content = null) =>
        Explain(rootPath, fullPath, depth, content).ToDirectoryEvaluation();

    /// <summary>
    /// Evaluates <paramref name="path"/> as the scan would reach it from <paramref name="rootPath"/>:
    /// every folder in between is evaluated too, and the first one the scan would not enter is reported,
    /// because a folder below a skipped or shallow folder is never reached, whatever its own rule says.
    /// Nothing is scanned; <paramref name="probe"/> lists a folder's files when a decision depends on them.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> does not lie under <paramref name="rootPath"/>.</exception>
    public RuleCheck Check(string rootPath, string path, Func<string, DirectoryContent?> probe)
    {
        var chain = new List<string>();
        for (var current = path; !SamePath(current, rootPath); current = Path.GetDirectoryName(current)!)
        {
            if (Path.GetDirectoryName(current) == null)
                throw new ArgumentException($"'{path}' does not lie under the scan root '{rootPath}'.", nameof(path));
            chain.Insert(0, current);
        }

        var evaluation = Explain(rootPath, rootPath, 0);
        string? blockedAt = null;
        RuleEvaluation? blockedBy = null;
        for (var i = 0; i < chain.Count; i++)
        {
            var folder = chain[i];
            evaluation = Explain(rootPath, folder, i + 1, () => probe(folder));
            if (i < chain.Count - 1 && blockedBy == null && evaluation.Decision != ScanDecision.Descend)
            {
                blockedAt = folder;
                blockedBy = evaluation;
            }
        }
        return new RuleCheck(path, rootPath, evaluation, blockedBy == null, blockedAt, blockedBy);
    }

    private static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    public RuleEvaluation Explain(string rootPath, string fullPath, int depth, Func<DirectoryContent?>? content = null)
    {
        if (depth == 0)
            return Result(ScanDecision.Descend, "Descend: scan root", DecisionBasis.ScanRoot, depth);

        var segments = PathSegments.Split(fullPath);
        var belowRoot = BelowRootStart(PathSegments.Split(rootPath), segments);
        var match = DeepestMatch(segments, belowRoot);

        if (match is { Rule.Action: ScanRuleAction.Skip })
            return RuleResult(ScanDecision.Skip, match.Value, depth, string.Empty);

        if (depth >= _rules.SafetyLimit)
            return Result(ScanDecision.Shallow,
                $"Shallow: depth {depth} reaches the safety limit {_rules.SafetyLimit} of rules '{_rules.Name}'",
                DecisionBasis.SafetyLimit, depth);

        if (match is { Rule.Action: ScanRuleAction.Shallow } shallow)
        {
            var levelsBelow = segments.Length - 1 - shallow.End;
            if (levelsBelow >= shallow.Rule.MaxDepth)
            {
                var detail = shallow.Rule.MaxDepth == 0 ? string.Empty : $", {levelsBelow} level(s) below the match, max_depth {shallow.Rule.MaxDepth}";
                return RuleResult(ScanDecision.Shallow, shallow, depth, detail);
            }
            return RuleResult(ScanDecision.Descend, shallow, depth,
                $", {levelsBelow} level(s) below the match, within max_depth {shallow.Rule.MaxDepth}");
        }

        if (match is { Rule.Action: ScanRuleAction.UserData } userData)
            return RuleResult(ScanDecision.Descend, userData, depth, string.Empty);

        if (_rules.SkipHidden && segments.Length > 0 && segments[^1].StartsWith('.'))
            return Result(ScanDecision.Skip, "Skip: hidden directory (name starts with a dot, no rule matches)", DecisionBasis.Hidden, depth);

        if (depth < _rules.ShallowFrom)
            return Result(ScanDecision.Descend, "Descend: no rule matches", DecisionBasis.NoRule, depth);

        return ByContent(depth, content);
    }

    public bool IsLikelyUserData(string fullPath)
    {
        var segments = PathSegments.Split(fullPath);
        return _rules.Rules.Any(r => r.Action == ScanRuleAction.UserData && LastRunEnd(segments, 0, r.Segments) >= 0);
    }

    private RuleEvaluation ByContent(int depth, Func<DirectoryContent?>? probe)
    {
        var where = $"depth {depth} reaches shallow_from {_rules.ShallowFrom}";
        var content = probe?.Invoke();
        if (content == null)
            return Result(ScanDecision.Descend, $"Descend: {where}, content not known; descending to be safe",
                DecisionBasis.ContentUnknown, depth);

        var user = content.Count(_rules.UserTypes);
        var program = content.Count(_rules.ProgramTypes);

        if (user > 0)
            return Result(ScanDecision.Descend,
                $"Descend: {where}, but the folder holds {user} user file(s) ({Describe(content, _rules.UserTypes)})",
                DecisionBasis.UserContent, depth, content, user, program);

        if (program > 0)
            return Result(ScanDecision.Shallow,
                $"Shallow: {where} and the folder looks like a program folder ({program} program file(s): {Describe(content, _rules.ProgramTypes)}; no user files)",
                DecisionBasis.ProgramContent, depth, content, user, program);

        return Result(ScanDecision.Descend,
            $"Descend: {where}, but the folder holds no program files; descending to be safe",
            DecisionBasis.NoProgramContent, depth, content, user, program);
    }

    private static string Describe(DirectoryContent content, IEnumerable<FileType> types) =>
        string.Join(", ", types
            .Where(t => content.CountsByType.GetValueOrDefault(t) > 0)
            .Select(t => $"{content.CountsByType[t]} {ScanRulesFile.TypeName(t)}"));

    private RuleEvaluation RuleResult(ScanDecision decision, Match match, int depth, string detail)
    {
        var why = match.Rule.Why is { } w ? $": {w}" : string.Empty;
        var reason = $"{decision}: matches {ScanRulesFile.ActionName(match.Rule.Action)} rule '{match.Rule.Match}' of rules '{_rules.Name}'{detail}{why}";
        return new RuleEvaluation(decision, reason, DecisionBasis.Rule, depth, match.Rule, match.Index, null, null, null);
    }

    private static RuleEvaluation Result(
        ScanDecision decision, string reason, DecisionBasis basis, int depth,
        DirectoryContent? content = null, int? user = null, int? program = null) =>
        new(decision, reason, basis, depth, null, null, user, program, content?.FileCount);

    private readonly record struct Match(ScanRule Rule, int Index, int End);

    /// <summary>The rule whose match ends closest to the folder; on a tie the one listed first.</summary>
    private Match? DeepestMatch(string[] segments, int belowRoot)
    {
        Match? best = null;
        for (var i = 0; i < _rules.Rules.Count; i++)
        {
            var rule = _rules.Rules[i];
            var from = rule.Action == ScanRuleAction.UserData ? 0 : belowRoot;
            var end = LastRunEnd(segments, from, rule.Segments);
            if (end >= 0 && (best == null || end > best.Value.End))
                best = new Match(rule, i, end);
        }
        return best;
    }

    /// <summary>Index of the first segment below <paramref name="root"/>; 0 if the path does not lie
    /// under the root (stricter, so nothing is wrongly kept).</summary>
    private static int BelowRootStart(string[] root, string[] path)
    {
        if (root.Length > path.Length)
            return 0;
        for (var i = 0; i < root.Length; i++)
        {
            if (!string.Equals(root[i], path[i], StringComparison.OrdinalIgnoreCase))
                return 0;
        }
        return root.Length;
    }

    /// <summary>Index of the last segment of the last occurrence of <paramref name="run"/> as consecutive
    /// whole segments at or after <paramref name="from"/>; -1 if it does not occur.</summary>
    private static int LastRunEnd(string[] segments, int from, IReadOnlyList<string> run)
    {
        if (run.Count == 0)
            return -1;
        for (var start = segments.Length - run.Count; start >= from; start--)
        {
            var match = true;
            for (var j = 0; j < run.Count && match; j++)
                match = string.Equals(segments[start + j], run[j], StringComparison.OrdinalIgnoreCase);
            if (match)
                return start + run.Count - 1;
        }
        return -1;
    }
}
