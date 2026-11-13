using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// A plan for one source job (https://github.com/crack666/smart_file_organizer/issues/11): what should happen to the
/// accepted findings, action by action. Stored in <c>plans</c>; nothing in it is executed by the plan.
/// </summary>
public sealed class Plan
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>The library jobs whose roots targets may lie in.</summary>
    public IReadOnlyList<long> Libraries { get; set; } = [];

    /// <summary>What the creation left out and why (backed-up files, findings without files).</summary>
    public IReadOnlyList<string> Notes { get; set; } = [];
}

/// <summary>One action of a plan (<c>plan_actions</c>).</summary>
public sealed class PlanAction
{
    public long Id { get; set; }
    public long PlanId { get; set; }

    /// <summary>The finding the action comes from; its unit groups the actions of a plan.</summary>
    public string FindingKey { get; set; } = string.Empty;

    public FindingKind FindingKind { get; set; }

    /// <summary>The title of the finding: what and where, in one line.</summary>
    public string Unit { get; set; } = string.Empty;

    public PlanSourceKind SourceKind { get; set; }

    /// <summary>Full path of the file or folder.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary><see cref="SourcePath"/> relative to the job root, with <c>/</c>.</summary>
    public string RelativePath { get; set; } = string.Empty;

    public long? FileNodeId { get; set; }
    public long? DirectoryNodeId { get; set; }
    public long Files { get; set; }
    public long Bytes { get; set; }

    public PlanActionKind Kind { get; set; } = PlanActionKind.Leave;

    /// <summary>What the finding recommended; differs from <see cref="Kind"/> when a check turned it into <c>leave</c>.</summary>
    public PlanActionKind Recommended { get; set; } = PlanActionKind.Leave;

    /// <summary>
    /// Full path of the file or folder at the target. For <c>delete_source</c> the place in the library that already
    /// holds the content.
    /// </summary>
    public string? TargetPath { get; set; }

    /// <summary>The library job whose root holds <see cref="TargetPath"/>.</summary>
    public long? LibraryJobId { get; set; }

    public string Reason { get; set; } = string.Empty;

    /// <summary>What a person should know: why the kind became <c>leave</c>, that the content is in a library already.</summary>
    public string? Note { get; set; }

    public PlanActionOrigin Origin { get; set; } = PlanActionOrigin.Rule;
    public PlanActionState State { get; set; } = PlanActionState.Proposed;

    /// <summary>The source is marked sensitive; it is decided one by one, never in a batch.</summary>
    public bool Sensitive { get; set; }

    /// <summary>Only <c>sfo plan approve --action</c> confirms it, never <c>--dir</c> or <c>--all</c>.</summary>
    public bool SingleDecision { get; set; }

    /// <summary><c>human</c> or <c>agent</c>: who confirmed or rejected it last.</summary>
    public PlanActionOrigin? DecidedBy { get; set; }

    public DateTime? DecidedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
