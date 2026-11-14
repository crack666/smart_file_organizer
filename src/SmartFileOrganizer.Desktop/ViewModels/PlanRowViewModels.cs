using CommunityToolkit.Mvvm.ComponentModel;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>One action of the plan as a row: what happens to it, where to, why, who, and whether it is released.</summary>
public partial class PlanActionRowViewModel : ViewModelBase
{
    /// <param name="libraryRoots">Roots of the plan's libraries: a target is shown below the name of its library.</param>
    /// <param name="keepSource">The job's switch "leave the source untouched": no original goes into the hold folder.</param>
    public PlanActionRowViewModel(PlanActionStatus status, IReadOnlyCollection<string>? libraryRoots = null, bool keepSource = false)
    {
        Status = status;
        var a = status.Action;
        var roots = libraryRoots ?? [];
        Name = a.SourceKind == PlanSourceKind.Folder
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(a.SourcePath)) + "/"
            : Path.GetFileName(a.SourcePath);
        RelativeFolder = Path.GetDirectoryName(a.RelativePath.Replace('/', Path.DirectorySeparatorChar)) is { Length: > 0 } folder
            ? folder.Replace(Path.DirectorySeparatorChar, '/')
            : ".";
        Outcome = PlanDisplay.OutcomeOf(a);
        // discard writes nowhere; a target it still carries is only kept for switching back to "Umziehen".
        ShortTargetText = a.Kind != PlanActionKind.Discard && a.TargetPath is { } t ? ShortTarget(t, roots) : "kein Ziel";
        TargetText = TargetLine(a, Outcome, ShortTargetText, keepSource);
        Consequence = PlanDisplay.Consequence(a, keepSource, t => ShortTarget(t, roots));
        SizeText = $"{PlanDisplay.Files(a.Files)}, {PlanDisplay.Size(a.Bytes)}";
        // After step 1 the target holds the plan's own checked copy; the executor does not count that as a collision
        // either, so problems are shown only while an action is still to be decided or run.
        HasProblems = status.Problems.Count > 0 && a.State is PlanActionState.Proposed or PlanActionState.Approved or PlanActionState.Rejected;
        ProblemsText = HasProblems ? string.Join(", ", status.Problems.Select(PlanDisplay.ProblemLabel)) : string.Empty;
    }

    /// <summary>The second line of a row: where the file is after executing, in the words of its outcome.</summary>
    private static string TargetLine(PlanAction a, PlanOutcome outcome, string target, bool keepSource) => outcome switch
    {
        PlanOutcome.Move when a.Kind == PlanActionKind.Copy || keepSource => $"→ {target} · Original bleibt",
        PlanOutcome.Move => "→ " + target,
        PlanOutcome.Bin when keepSource => "bleibt (Quelle nicht anfassen)",
        PlanOutcome.Bin when a.Kind == PlanActionKind.Discard => "Papierkorb · ohne Kopie",
        PlanOutcome.Bin when a.Kind == PlanActionKind.Quarantine => $"Papierkorb · Kopie nach {target}",
        PlanOutcome.Bin => $"Papierkorb · liegt schon in {target}",
        _ when a.State == PlanActionState.Rejected => "bleibt hier · abgelehnt",
        _ => "bleibt hier"
    };

    /// <summary>A target below a library root as "library/rest"; any other path as it is.</summary>
    public static string ShortTarget(string target, IReadOnlyCollection<string> roots)
    {
        var root = roots.FirstOrDefault(r => PlanChecks.IsAtOrBelow(target, r));
        return root is null
            ? target
            : Path.Combine(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), Path.GetRelativePath(root, target)).Replace('\\', '/');
    }

    public PlanActionStatus Status { get; }
    public PlanAction Action => Status.Action;
    public long Id => Action.Id;

    public string Name { get; }

    /// <summary>The folder of the source relative to the job root, with <c>/</c>.</summary>
    public string RelativeFolder { get; }

    public string TargetText { get; }

    /// <summary>The target as "library/rest", or "kein Ziel".</summary>
    public string ShortTargetText { get; }

    public string SizeText { get; }

    /// <summary>What happens to the file: the outcome of its kind, or <see cref="PlanOutcome.Stay"/> when rejected.</summary>
    public PlanOutcome Outcome { get; }

    public string OutcomeLabel => PlanDisplay.OutcomeLabel(Outcome);
    public string OutcomeColor => PlanDisplay.OutcomeColor(Outcome);

    /// <summary>What executing it does, where the file is afterwards and whether it can be taken back.</summary>
    public string Consequence { get; }

    public string KindLabel => PlanDisplay.KindLabel(Action.Kind);
    public string KindColor => PlanDisplay.KindColor(Action.Kind);
    public string KindTip => $"{Consequence}{Environment.NewLine}Code: {Action.Kind.ToCode()}, {Action.State.ToCode()} (empfohlen: {Action.Recommended.ToCode()})";

    /// <summary>Released (approved) for execution.</summary>
    public bool IsReleased => Action.State == PlanActionState.Approved;

    public string StateLabel => PlanDisplay.StateLabel(Action);
    public string StateColor => PlanDisplay.StateColor(Action.State);

    /// <summary>A steering agent confirmed or rejected it; the person may want to look again.</summary>
    public bool DecidedByAgent => Action.DecidedBy == PlanActionOrigin.Agent
                                  && Action.State is PlanActionState.Approved or PlanActionState.Rejected;

    public string OriginText => "Herkunft: " + PlanDisplay.OriginLabel(Action.Origin);
    public bool IsSensitive => Action.Sensitive;
    public bool HasProblems { get; }
    public string ProblemsText { get; }
    public bool ChangedFromRecommendation => Action.Kind != Action.Recommended;

    /// <summary>Ticked for the buttons that act on a selection.</summary>
    [ObservableProperty] private bool _isChecked;
}

/// <summary>A group of actions: one bundle (the finding they come from) or one source folder.</summary>
public partial class PlanGroupViewModel : ViewModelBase
{
    public PlanGroupViewModel(string key, string title, string subtitle, IReadOnlyList<PlanActionRowViewModel> actions)
    {
        Key = key;
        Title = title;
        Subtitle = subtitle;
        Actions = actions;
        var counts = actions.GroupBy(r => r.Outcome).OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {PlanDisplay.OutcomeLabel(g.Key)}");
        var released = actions.Count(r => r.IsReleased);
        Summary = $"{(actions.Count == 1 ? "1 Aktion" : $"{actions.Count} Aktionen")} · {PlanDisplay.Size(actions.Sum(r => r.Action.Bytes))} · "
                  + $"{string.Join(", ", counts)} · freigegeben {released} von {actions.Count}";
    }

    public string Key { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Summary { get; }
    public IReadOnlyList<PlanActionRowViewModel> Actions { get; }

    [ObservableProperty] private bool _isExpanded = true;

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));
}

/// <summary>A kind as the finer switch of the detail offers it.</summary>
public sealed record PlanKindOption(PlanActionKind Kind)
{
    public string Label => PlanDisplay.KindLabel(Kind);
    public string Color => PlanDisplay.KindColor(Kind);
    public string Code => Kind.ToCode();
}

/// <summary>One of the three outcome buttons of the detail: filled in its colour when it is the action's outcome.</summary>
public sealed record PlanOutcomeChoice(PlanOutcome Outcome, bool IsCurrent)
{
    public string Label => PlanDisplay.OutcomeButton(Outcome);
    public string Hint => PlanDisplay.OutcomeHint(Outcome);
    public string Background => IsCurrent ? PlanDisplay.OutcomeColor(Outcome) : "#333337";
    public string Border => PlanDisplay.OutcomeColor(Outcome);
}

/// <summary>How the actions are grouped.</summary>
public sealed record PlanGrouping(string Code, string Label)
{
    public static PlanGrouping Bundle { get; } = new("bundle", "nach Bündel");
    public static PlanGrouping Folder { get; } = new("folder", "nach Ordner");
}

/// <summary>A plan in the picker.</summary>
public sealed record PlanListItem(Plan Plan)
{
    public string Label => $"Plan {Plan.Id} · {Plan.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm}";
}

/// <summary>A source job in the picker.</summary>
public sealed record PlanJobItem(ScanJob Job)
{
    public string Label => $"Job {Job.Id} · {Job.RootPath}";
}

/// <summary>One line of a run result: what happened to an action and why.</summary>
public sealed record PlanRunLine(long ActionId, string Name, string Outcome, string Reasons, string Detail, bool IsProblem)
{
    public string OutcomeColor => IsProblem ? PlanDisplay.ProblemColor : "#A0A7B0";
}
