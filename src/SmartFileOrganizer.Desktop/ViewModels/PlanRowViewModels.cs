using CommunityToolkit.Mvvm.ComponentModel;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>One action of the plan as a row: what, where to, how, why, who, and where it stands.</summary>
public partial class PlanActionRowViewModel : ViewModelBase
{
    /// <param name="libraryRoots">Roots of the plan's libraries: a target is shown below the name of its library.</param>
    public PlanActionRowViewModel(PlanActionStatus status, IReadOnlyCollection<string>? libraryRoots = null)
    {
        Status = status;
        var a = status.Action;
        Name = a.SourceKind == PlanSourceKind.Folder
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(a.SourcePath)) + "/"
            : Path.GetFileName(a.SourcePath);
        RelativeFolder = Path.GetDirectoryName(a.RelativePath.Replace('/', Path.DirectorySeparatorChar)) is { Length: > 0 } folder
            ? folder.Replace(Path.DirectorySeparatorChar, '/')
            : ".";
        TargetText = a.TargetPath is { } t ? "→ " + ShortTarget(t, libraryRoots ?? []) : "kein Ziel";
        SizeText = $"{PlanDisplay.Files(a.Files)}, {PlanDisplay.Size(a.Bytes)}";
        // After step 1 the target holds the plan's own checked copy; the executor does not count that as a collision
        // either, so problems are shown only while an action is still to be decided or run.
        HasProblems = status.Problems.Count > 0 && a.State is PlanActionState.Proposed or PlanActionState.Approved or PlanActionState.Rejected;
        ProblemsText = HasProblems ? string.Join(", ", status.Problems.Select(p => p.ToCode())) : string.Empty;
    }

    private static string ShortTarget(string target, IReadOnlyCollection<string> roots)
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
    public string SizeText { get; }

    public string KindLabel => PlanDisplay.KindLabel(Action.Kind);
    public string KindColor => PlanDisplay.KindColor(Action.Kind);
    public string KindTip => $"{Action.Kind.ToCode()} (empfohlen: {Action.Recommended.ToCode()})";

    public string StateLabel => PlanDisplay.StateLabel(Action.State);
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
        var counts = actions.GroupBy(r => r.Action.Kind).OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {PlanDisplay.KindLabel(g.Key)}");
        Summary = $"{(actions.Count == 1 ? "1 Aktion" : $"{actions.Count} Aktionen")} · {PlanDisplay.Size(actions.Sum(r => r.Action.Bytes))} · {string.Join(", ", counts)}";
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

/// <summary>A kind as the switch offers it.</summary>
public sealed record PlanKindOption(PlanActionKind Kind)
{
    public string Label => PlanDisplay.KindLabel(Kind);
    public string Color => PlanDisplay.KindColor(Kind);
    public string Code => Kind.ToCode();
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
