using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// One input a decision rests on: a fact of the file, the category of its result, the verdict of its folder, a
/// bundle, the file type or a sensitive marker. <paramref name="Source"/> says where it comes from (<c>metadata</c>,
/// <c>rule</c>, <c>model</c>, the origin of a result such as <c>human</c>, <c>scan</c>, <c>bundle</c>); for a
/// marker, the rule ids that set it. Codes are in docs/glossary.md, section Decisions.
/// </summary>
public sealed record DecisionInput(string Key, string Value, string Source);

/// <summary>
/// Where a kept file should go, and why (https://github.com/crack666/smart_file_organizer/issues/37). A proposal
/// always names the inputs it rests on; nothing is moved.
/// </summary>
/// <param name="TargetId">The id of the target in the target structure (<c>photos</c>).</param>
/// <param name="Path">Relative to the root of the target structure, with <c>/</c>; the folder level of the pattern is
/// left out when one of its placeholders has no fact.</param>
/// <param name="Rule">What chose the target: <c>target_structure</c> (the first target whose <c>holds</c> names the file type).</param>
/// <param name="Basis">The inputs the target and every filled placeholder rest on.</param>
/// <param name="Unresolved">Placeholders of the pattern no fact could fill (<c>occasion</c>).</param>
public sealed record TargetProposal(
    string TargetId,
    string Path,
    string Rule,
    IReadOnlyList<DecisionInput> Basis,
    IReadOnlyList<string> Unresolved);

/// <summary>
/// Stage 2 of a file (https://github.com/crack666/smart_file_organizer/issues/37): its worth and a target, computed by
/// rules from stored facts, the result of the analysis, the folder verdict and the bundles, without a model call.
/// Stored in <c>file_decisions</c>, replaced by every <c>sfo decide</c>.
/// </summary>
public sealed record FileDecision
{
    public long JobId { get; init; }
    public long FileNodeId { get; init; }

    /// <summary><c>keep</c> or <c>disposable</c>; null when the stored results say nothing about the file.</summary>
    public RetentionValue? Worth { get; init; }

    public ResultCertainty Certainty { get; init; }

    /// <summary>What decided the worth: <c>human</c>, <c>agent</c>, <c>model</c>, <c>rule</c>, <c>inherited</c>,
    /// <c>facts</c>, <c>directory</c>, <c>bundle</c>, <c>file_type</c>, <c>name</c>, <c>sensitive</c> or <c>none</c>.</summary>
    public string Rule { get; init; } = "none";

    public IReadOnlyList<DecisionInput> Basis { get; init; } = [];

    /// <summary>The target of a kept file; null without a target structure, for a marked file, or when no target holds its type.</summary>
    public TargetProposal? Target { get; init; }

    public DateTime DecidedAt { get; init; }
}
