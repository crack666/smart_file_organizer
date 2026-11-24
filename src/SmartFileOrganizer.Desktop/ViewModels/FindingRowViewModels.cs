using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>One finding as a row: kind, what and where, period, size, the recommended action with its target, the decision.</summary>
public sealed class FindingRowViewModel : ViewModelBase
{
    private readonly Func<DisplayLanguage> _language;
    private readonly string? _decidedTarget;

    /// <param name="decidedTarget">The target <c>sfo decide</c> gives all files of the finding, when it has no place of its own.</param>
    public FindingRowViewModel(Finding finding, Func<DisplayLanguage> language, string? decidedTarget = null)
    {
        Finding = finding;
        _language = language;
        _decidedTarget = decidedTarget;
        FileIds = finding.FileIds.ToHashSet();
    }

    private DisplayLanguage Language => _language();

    public Finding Finding { get; }
    public string Key => Finding.Key;

    /// <summary>The files of the finding; a search finds it by them.</summary>
    public IReadOnlySet<long> FileIds { get; }

    public string KindLabel => CodeLabels.Label(Finding.Kind, Language);
    public string KindColor => FindingDisplay.KindColor(Finding.Kind);
    public string StateLabel => CodeLabels.Label(Finding.State, Language);
    public string StateColor => FindingDisplay.StateColor(Finding.State);
    public string CertaintyLabel => CodeLabels.Label(Finding.Certainty, Language);
    public string CertaintyColor => FindingDisplay.CertaintyColor(Finding.Certainty);
    public bool IsOpen => Finding.State == FindingState.Open;

    /// <summary>Decided one by one, never in a batch (sensitive files).</summary>
    public bool SingleDecision => Finding.Action.SingleDecision;

    /// <summary>The marker words of a sensitive finding, <c>Zugangsdaten, Finanzen</c>.</summary>
    public string MarkersText => CodeLabels.Markers(Finding.Markers, Language);

    public string Place => FindingDisplay.Place(Finding.RelativePath);
    public string FilesText => FindingDisplay.Files(Finding.Group, Finding.Files, Language);
    public string SizeText => PlanDisplay.Size(Finding.Bytes);
    public string PeriodText => FindingDisplay.Period(Finding.Period?.From, Finding.Period?.To);

    /// <summary>"3 Bilder · Urlaub Kroatien 2021".</summary>
    public string Title => Finding.Files == 0 ? Place : $"{FilesText} · {Place}";

    /// <summary>Period, size and markers.</summary>
    public string FactsText => string.Join(" · ", new[] { PeriodText, SizeText, MarkersText }.Where(t => t.Length > 0));

    public string ActionLabel => CodeLabels.Label(Finding.Action.Kind, Language);
    public string ActionColor => PlanDisplay.OutcomeColor(PlanDisplay.OutcomeOf(Finding.Action.Kind));

    /// <summary>Where it would go: "bibliothek/Fotos/2021", what <c>sfo decide</c> says, or why there is no target yet.</summary>
    public string TargetText
    {
        get
        {
            if (Finding.Target is { LibraryRoot: { } root } t)
                return PlanActionRowViewModel.ShortTarget(t.Path, [root]);
            if (Finding.Target is { } other)
                return other.RelativePath;
            if (Finding.TargetParts.Count > 0)
                return $"{CodeLabels.Label(FindingTargetStatus.Split, Language)} ({Finding.TargetParts.Count})";
            if (_decidedTarget is not null && !IsTargetDecided)
                return _decidedTarget + " (laut Zielstruktur)";
            return Finding.TargetStatus is { } status ? CodeLabels.Label(status, Language) : string.Empty;
        }
    }

    /// <summary>Action and target; for duplicates how many copies it concerns (one stays).</summary>
    public string ActionText => TargetText.Length > 0 ? $"{ActionLabel} → {TargetText}"
        : Finding is { Duplicates: { } d, Action.Kind: not PlanActionKind.Leave }
            ? $"{ActionLabel}: {d.RedundantFiles} {(Language == DisplayLanguage.German ? "überzählig" : "redundant")}"
        : ActionLabel;

    /// <summary>True for the kinds that get a place in a library.</summary>
    public bool CanPlace => FindingRules.TargetKinds.Contains(Finding.Kind) && Finding.TargetInput is not null;

    /// <summary>A person or an agent set the target; it can be taken back.</summary>
    public bool IsTargetDecided => Finding.TargetDecidedBy == TargetDecidedBy.Controller;

    /// <summary>A quick filter's answer for this finding.</summary>
    public bool Is(FindingQuickFilter filter) => filter switch
    {
        FindingQuickFilter.NotBackedUp => Finding.Kind == FindingKind.NotBackedUp,
        FindingQuickFilter.Sensitive => Finding.Kind == FindingKind.Sensitive,
        FindingQuickFilter.Review => Finding.Unsure,
        _ => Finding.Kind == FindingKind.Duplicate
    };

    /// <summary>True when the search finds the finding itself or one of its files.</summary>
    public bool Matches(JobSearchResult search) =>
        FileIds.Any(search.FileIds.Contains)
        || JobSearchService.Matches(search.Terms, Finding.Title, Finding.Reason, Title, KindLabel, Finding.RelativePath, TargetText);

    /// <summary>The finding was decided, or the language changed: every word again.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>A group of the list: a bundle with the findings in it, or the findings outside every bundle.</summary>
public partial class FindingGroupViewModel : ViewModelBase
{
    private readonly Func<DisplayLanguage> _language;

    public FindingGroupViewModel(
        string key, BundleItem? item, IReadOnlyList<FindingRowViewModel> findings, string? parentName, Func<DisplayLanguage> language)
    {
        Key = key;
        Item = item;
        Findings = findings;
        ParentName = parentName;
        _language = language;
    }

    public string Key { get; }

    /// <summary>The bundle; null for the findings outside every bundle.</summary>
    public BundleItem? Item { get; }

    public Bundle? Bundle => Item?.Bundle;
    public IReadOnlyList<FindingRowViewModel> Findings { get; }
    public string? ParentName { get; }

    public string KindLabel => Bundle is { } b ? CodeLabels.Label(b.Kind, _language()) : string.Empty;
    public string Title => Bundle is null ? "Ohne Bündel" : Bundle.SuggestedName is { Length: > 0 } n ? n : $"Bündel {Bundle.Id}";
    public string PeriodText => Bundle is { } b ? FindingDisplay.Period(b.FirstTime, b.LastTime) : string.Empty;

    /// <summary>Kind, period, files, size and how much of it is still to decide.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Bundle is { } b)
            {
                parts.Add(KindLabel);
                if (PeriodText.Length > 0) parts.Add(PeriodText);
                parts.Add(PlanDisplay.Files(b.FileCount));
                parts.Add(PlanDisplay.Size(b.TotalBytes));
                if (Item!.SensitiveFiles > 0) parts.Add($"{Item.SensitiveFiles} sensibel");
                if (ParentName is { } p) parts.Add("in " + p);
            }
            parts.Add(Findings.Count switch
            {
                0 => "kein Fundstück",
                1 => "1 Fundstück",
                var n => $"{n} Fundstücke"
            } + (Findings.Count(f => f.IsOpen) is > 0 and var open ? $", {open} offen" : string.Empty));
            return string.Join(" · ", parts);
        }
    }

    public bool HasFindings => Findings.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded = true;

    public string ExpandGlyph => !HasFindings ? "·" : IsExpanded ? "▾" : "▸";

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>The quick filters of the side bar.</summary>
public enum FindingQuickFilter { NotBackedUp, Sensitive, Review, Duplicate }

/// <summary>One quick filter with how many findings of the open job it lets through.</summary>
public partial class FindingQuickFilterViewModel(FindingQuickFilter filter) : ViewModelBase
{
    public FindingQuickFilter Filter { get; } = filter;

    public string Label => Filter switch
    {
        FindingQuickFilter.NotBackedUp => "nicht gesichert",
        FindingQuickFilter.Sensitive => "sensibel",
        FindingQuickFilter.Review => "prüfen",
        _ => "doppelt"
    };

    public string Hint => Filter switch
    {
        FindingQuickFilter.NotBackedUp => "Aufhebenswert und in keiner Bibliothek",
        FindingQuickFilter.Sensitive => "Markierte Dateien; immer einzeln zu entscheiden",
        FindingQuickFilter.Review => "Unsichere Fundstücke; zusammen mit einem anderen Filter nur die unsicheren davon",
        _ => "Gleicher Inhalt an mehreren Stellen"
    };

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private int _count;
}

/// <summary>Which decided findings the list shows.</summary>
public sealed record FindingStateFilter(string Label, IReadOnlyList<FindingState> States)
{
    public static FindingStateFilter Default { get; } = new("offen und angenommen", [FindingState.Open, FindingState.Accepted]);
    public static FindingStateFilter Open { get; } = new("nur offen", [FindingState.Open]);
    public static FindingStateFilter Refused { get; } = new("abgelehnt und falsch", [FindingState.Rejected, FindingState.Wrong]);
    public static FindingStateFilter Every { get; } = new("alle", Enum.GetValues<FindingState>());

    public static IReadOnlyList<FindingStateFilter> All { get; } =
        [Default, Open, new("nur angenommen", [FindingState.Accepted]), Refused, Every];

    public override string ToString() => Label;
}

/// <summary>How the list is ordered.</summary>
public sealed record FindingGrouping(string Label)
{
    public static FindingGrouping Bundle { get; } = new("nach Bündel");
    public static FindingGrouping Rank { get; } = new("nach Wichtigkeit");
    public static IReadOnlyList<FindingGrouping> All { get; } = [Bundle, Rank];

    public override string ToString() => Label;
}

/// <summary>The language of the codes.</summary>
public sealed record DisplayLanguageOption(string Label, DisplayLanguage Language)
{
    public static IReadOnlyList<DisplayLanguageOption> All { get; } =
        [new("Deutsch", DisplayLanguage.German), new("English", DisplayLanguage.English)];

    public override string ToString() => Label;
}

/// <summary>One picture of the strip of a finding or bundle.</summary>
public sealed record FindingPicture(Bitmap Image, string Name, string FullPath);

/// <summary>One file in the list of a finding or bundle.</summary>
public sealed record FindingFileItem(string RelativePath, string FullPath, string FactsText, string Marker, string Summary)
{
    public bool HasMarker => Marker.Length > 0;
    public bool HasSummary => Summary.Length > 0;
}
