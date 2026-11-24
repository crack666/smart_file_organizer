using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Desktop.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// The findings and bundles of a source job, the first thing the person sees of it
/// (https://github.com/crack666/smart_file_organizer/issues/27): accept, reject or change the target of one or many, with
/// the keyboard, with a preview of what they hold. Findings are computed by <see cref="FindingService"/> (stored data only,
/// no model), decisions go to <c>finding_decisions</c> with the origin <c>human</c>, targets through
/// <see cref="FindingPlacementService"/> and its checks. Nothing here changes a file; the plan turns accepted findings
/// into actions.
/// </summary>
public partial class FindingsViewModel : ViewModelBase
{
    /// <summary>Pictures in the strip of a finding or bundle.</summary>
    public const int MaxPictures = 8;

    /// <summary>Files listed in the detail; the rest is counted.</summary>
    public const int MaxListedFiles = 200;

    private const int PictureWidth = 240;
    private static readonly TimeSpan DetailDelay = TimeSpan.FromMilliseconds(60);

    /// <summary>The reason of a target the person set without a note.</summary>
    public const string PlacedByHand = "Von Hand gewählt";

    private readonly FindingService _findings;
    private readonly BundleService _bundles;
    private readonly FindingPlacementService _placement;
    private readonly IFileDecisionRepository _fileDecisions;
    private readonly LibraryTargetService _libraries;
    private readonly ScanJobService _jobs;
    private readonly FileQueryService _files;
    private readonly FilePreviewService _preview;
    private readonly IShellLauncher _shell;
    private readonly SensitiveOutputOptions _sensitive;

    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private IReadOnlyList<FindingRowViewModel> _all = [];
    private IReadOnlyList<BundleItem> _bundleItems = [];
    private JobSearchResult? _search;
    private ScanJob? _job;
    private int _loadVersion;
    private bool _rebuilding;
    private bool _deciding;
    private CancellationTokenSource? _detailCts;
    private object? _detailFor;
    private string? _revealedKey;

    public FindingsViewModel(
        FindingService findings,
        BundleService bundles,
        FindingPlacementService placement,
        IFileDecisionRepository fileDecisions,
        LibraryTargetService libraries,
        ScanJobService jobs,
        FileQueryService files,
        FilePreviewService preview,
        IShellLauncher shell,
        SensitiveOutputOptions sensitive)
    {
        _findings = findings;
        _bundles = bundles;
        _placement = placement;
        _fileDecisions = fileDecisions;
        _libraries = libraries;
        _jobs = jobs;
        _files = files;
        _preview = preview;
        _shell = shell;
        // As in the other views: a person at the local machine sees what is marked, the text of access data never.
        _sensitive = sensitive with { IncludeSensitive = true };
        QuickFilters = Enum.GetValues<FindingQuickFilter>().Select(f => new FindingQuickFilterViewModel(f)).ToList();
        foreach (var f in QuickFilters)
            f.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(FindingQuickFilterViewModel.IsActive)) return;
                OnPropertyChanged(nameof(IsFiltered));
                Rebuild();
            };
        SelectedRows.CollectionChanged += OnSelectionChanged;
    }

    // ------------------------------------------------------------------ list

    /// <summary>The findings as they were computed last; null while none are loaded.</summary>
    public FindingReport? Report { get; private set; }

    /// <summary>Group headers and findings in display order.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    /// <summary>The selected rows of the list; the last one fills the detail.</summary>
    public ObservableCollection<object> SelectedRows { get; } = [];

    /// <summary>Every finding of the job, whatever the filters show.</summary>
    public IReadOnlyList<FindingRowViewModel> All => _all;

    public IReadOnlyList<FindingQuickFilterViewModel> QuickFilters { get; }
    public IReadOnlyList<FindingStateFilter> StateFilters => FindingStateFilter.All;
    public IReadOnlyList<FindingGrouping> Groupings => FindingGrouping.All;
    public IReadOnlyList<DisplayLanguageOption> Languages => DisplayLanguageOption.All;

    [ObservableProperty] private FindingStateFilter _selectedStateFilter = FindingStateFilter.Default;
    [ObservableProperty] private FindingGrouping _selectedGrouping = FindingGrouping.Bundle;
    [ObservableProperty] private DisplayLanguageOption _selectedLanguage = DisplayLanguageOption.All[0];

    [ObservableProperty] private bool _isLoading;

    /// <summary>"8 Fundstücke · 5 offen · 2 angenommen".</summary>
    [ObservableProperty] private string _countText = string.Empty;

    /// <summary>What was not computed because a step is missing, with the command that does it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissing))]
    private string _missingText = string.Empty;

    [ObservableProperty] private string _missingToolTip = string.Empty;

    public bool HasMissing => MissingText.Length > 0;

    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>Why the list is empty; empty when it shows rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyText))]
    private string _emptyText = string.Empty;

    public bool HasEmptyText => EmptyText.Length > 0;

    public DisplayLanguage Language => SelectedLanguage.Language;

    public bool IsFiltered => QuickFilters.Any(f => f.IsActive);

    partial void OnSelectedStateFilterChanged(FindingStateFilter value) => Rebuild();
    partial void OnSelectedGroupingChanged(FindingGrouping value) => Rebuild();

    partial void OnSelectedLanguageChanged(DisplayLanguageOption value)
    {
        foreach (var r in _all) r.Refresh();
        foreach (var g in Rows.OfType<FindingGroupViewModel>()) g.Refresh();
        ShowMissing();
        ShowTexts();
        ShowDetailTexts(_detailFor);
    }

    // ------------------------------------------------------------------ loading

    /// <summary>Shows a source job. Another job starts without selection, search, note and opened previews.</summary>
    public async Task LoadAsync(ScanJob job)
    {
        if (_job?.Id != job.Id)
        {
            Clear();
            _job = job;
        }
        await ReloadAsync();
    }

    public void Clear()
    {
        _loadVersion++;
        _job = null;
        Report = null;
        _all = [];
        _bundleItems = [];
        _search = null;
        _collapsed.Clear();
        _revealedKey = null;
        TargetPicker?.Cancel();
        Note = string.Empty;
        CountText = MissingText = StatusText = EmptyText = string.Empty;
        IsLoading = false;
        Rebuild();
    }

    /// <summary>Computes the findings again (after an analysis, or what an agent decided meanwhile); the selection stays.</summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        if (_job is not { } job) return;
        var version = ++_loadVersion;
        IsLoading = true;
        EmptyText = _all.Count == 0 ? "Fundstücke werden berechnet …" : string.Empty;
        try
        {
            // Off the UI thread: one pass over every file of the job, SQLite answers synchronously.
            var (report, bundles, decisions) = await Task.Run(async () => (
                await _findings.ComputeAsync(job.Id),
                await _bundles.GetAllAsync(job.Id),
                await _fileDecisions.GetByJobAsync(job.Id)));
            if (version != _loadVersion) return; // another job was opened meanwhile

            Report = report;
            _bundleItems = bundles;
            _all = report.Findings.Select(f => new FindingRowViewModel(f, () => Language, DecidedTarget(f, decisions))).ToList();
            ShowMissing();
            Rebuild();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version != _loadVersion) return;
            Report = null;
            _all = [];
            _bundleItems = [];
            MissingText = string.Empty;
            Rebuild();
            EmptyText = ex.Message;
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    /// <summary>The one target <c>sfo decide</c> gave every file of the finding; null when there is none or they differ.</summary>
    private static string? DecidedTarget(Finding f, IReadOnlyDictionary<long, FileDecision> decisions)
    {
        if (decisions.Count == 0 || f.FileIds.Count == 0 || !FindingRules.TargetKinds.Contains(f.Kind)) return null;
        var paths = f.FileIds
            .Select(id => decisions.TryGetValue(id, out var d) ? d.Target?.Path : null)
            .Distinct(StringComparer.Ordinal).Take(2).ToList();
        return paths.Count == 1 ? paths[0] : null;
    }

    // ------------------------------------------------------------------ filters and search

    /// <summary>Only findings (and bundles) the search finds; null ends the search.</summary>
    public void ApplySearch(JobSearchResult? search)
    {
        _search = search is { Terms.Count: > 0 } && search.JobId == _job?.Id ? search : null;
        Rebuild();
    }

    [RelayCommand]
    private void ResetFilters()
    {
        foreach (var f in QuickFilters) f.IsActive = false;
    }

    private bool PassesState(FindingRowViewModel r) => SelectedStateFilter.States.Contains(r.Finding.State);

    /// <summary>Kinds together with "or"; "prüfen" narrows what the kinds let through.</summary>
    private bool PassesQuick(FindingRowViewModel r)
    {
        var kinds = QuickFilters.Where(f => f.IsActive && f.Filter != FindingQuickFilter.Review).ToList();
        if (kinds.Count > 0 && !kinds.Any(f => r.Is(f.Filter))) return false;
        return !QuickFilters.Any(f => f is { IsActive: true, Filter: FindingQuickFilter.Review }) || r.Is(FindingQuickFilter.Review);
    }

    private bool PassesSearch(FindingRowViewModel r) => _search is null || r.Matches(_search);

    /// <summary>Fills the list from the filters; what stays in it keeps its selection.</summary>
    private void Rebuild()
    {
        var selected = SelectedRows.Select(KeyOf).ToList();
        var shown = _all.Where(r => PassesState(r) && PassesQuick(r) && PassesSearch(r)).ToList();
        foreach (var f in QuickFilters)
            f.Count = _all.Count(r => PassesState(r) && PassesSearch(r) && r.Is(f.Filter));

        _rebuilding = true;
        try
        {
            SelectedRows.Clear();
            Rows.Clear();
            if (SelectedGrouping == FindingGrouping.Bundle && _bundleItems.Count > 0)
                AddGroups(shown);
            else
                foreach (var r in shown)
                    Rows.Add(r);

            foreach (var key in selected)
                if (Rows.FirstOrDefault(r => KeyOf(r) == key) is { } again && !SelectedRows.Contains(again))
                    SelectedRows.Add(again);
        }
        finally
        {
            _rebuilding = false;
        }
        ShowTexts();
        SelectionSettled();
    }

    private void AddGroups(List<FindingRowViewModel> shown)
    {
        var byId = _bundleItems.ToDictionary(b => b.Bundle.Id);
        var byBundle = shown.ToLookup(r => BundleOf(r.Finding, byId));
        // A bundle without a finding is listed while no filter narrows the list; during a search only when the search names it.
        var withEmpty = !IsFiltered && SelectedStateFilter != FindingStateFilter.Refused;
        var groups = _bundleItems
            .Select(b => (Item: b, Rows: (IReadOnlyList<FindingRowViewModel>)byBundle[b.Bundle.Id].ToList()))
            .Where(g => g.Rows.Count > 0 || withEmpty && (_search is null
                || JobSearchService.Matches(_search.Terms, g.Item.Bundle.SuggestedName, g.Item.Bundle.RootPath, g.Item.Bundle.Reason)))
            .Select(g => new FindingGroupViewModel("bundle:" + g.Item.Bundle.Id, g.Item, g.Rows,
                g.Item.Bundle.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent.Bundle.SuggestedName : null, () => Language))
            .ToList();
        var loose = byBundle[0].ToList();
        if (loose.Count > 0)
            groups.Add(new FindingGroupViewModel("none", null, loose, null, () => Language));

        foreach (var g in groups
                     .OrderBy(g => g.Findings.Count == 0)
                     .ThenBy(g => g.Findings.Count == 0 ? 0 : g.Findings.Min(f => f.Finding.Rank))
                     .ThenByDescending(g => g.Bundle?.TotalBytes ?? 0))
        {
            g.IsExpanded = !_collapsed.Contains(g.Key);
            Rows.Add(g);
            if (!g.IsExpanded) continue;
            foreach (var r in g.Findings.OrderBy(f => f.Finding.Rank))
                Rows.Add(r);
        }
    }

    /// <summary>The bundle of a finding: its own, else the innermost bundle that is a folder and holds the place of the
    /// finding (a sensitive file lies in a bundle without being its subject); 0 for none.</summary>
    private static long BundleOf(Finding f, IReadOnlyDictionary<long, BundleItem> bundles)
    {
        if (f.BundleId is { } id) return bundles.ContainsKey(id) ? id : 0;
        if (f.Path is not { } path) return 0;
        return bundles.Values.Select(b => b.Bundle)
            .Where(b => b.RootPath is not null && b.Kind is not (BundleKind.Event or BundleKind.Scattered) && PlanChecks.IsAtOrBelow(path, b.RootPath))
            .MaxBy(b => b.RootPath!.Length)?.Id ?? 0;
    }

    private static string KeyOf(object row) => row switch
    {
        FindingRowViewModel r => r.Key,
        FindingGroupViewModel g => g.Key,
        _ => string.Empty
    };

    private void ShowMissing()
    {
        // In the strip what is missing entirely and the commands for it; what is only partly known in its tooltip.
        MissingText = Report is not { } report ? string.Empty : string.Join(Environment.NewLine,
            report.Coverage.Where(c => c.Value is { Coverage: FindingCoverage.NotComputed, Note.Length: > 0 }).Select(Line)
                .Concat(report.MissingSteps.Select(s => "Noch offen: " + s)));
        MissingToolTip = Report is null ? string.Empty : string.Join(Environment.NewLine,
            Report.Coverage.Where(c => c.Value.Note is { Length: > 0 }).Select(Line));

        string Line(KeyValuePair<FindingKind, KindCoverage> c) => $"{CodeLabels.Label(c.Key, Language)}: {c.Value.Note}";
    }

    private void ShowTexts()
    {
        var parts = new List<string> { _all.Count == 1 ? "1 Fundstück" : $"{_all.Count} Fundstücke" };
        parts.AddRange(Enum.GetValues<FindingState>()
            .Select(s => (State: s, Count: _all.Count(r => r.Finding.State == s)))
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Count} {CodeLabels.Label(x.State, Language)}"));
        var groups = Rows.OfType<FindingGroupViewModel>().ToList();
        var shown = groups.Count > 0 ? groups.Sum(g => g.Findings.Count) : Rows.Count;
        if (shown != _all.Count) parts.Add($"{shown} gezeigt");
        CountText = Report is null ? string.Empty : string.Join(" · ", parts);
        if (Rows.Count > 0 || _job is null) EmptyText = string.Empty;
        else if (Report is not null)
            EmptyText = _all.Count == 0 ? "Keine Fundstücke in diesem Job."
                : _search is not null ? "Nichts passt zur Suche." : "Kein Fundstück passt zu den Filtern.";
    }

    /// <summary>Opens or closes a bundle in the list.</summary>
    [RelayCommand]
    private void ToggleGroup(FindingGroupViewModel? group)
    {
        if (group is not { HasFindings: true }) return;
        if (!_collapsed.Remove(group.Key)) _collapsed.Add(group.Key);
        Rebuild();
    }

    // ------------------------------------------------------------------ selection

    /// <summary>The findings the buttons and keys act on: the selected ones and those of a selected bundle, as they are shown.</summary>
    public IReadOnlyList<FindingRowViewModel> SelectedFindings { get; private set; } = [];

    [ObservableProperty] private string _selectionText = string.Empty;

    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_rebuilding) SelectionSettled();
    }

    private void SelectionSettled()
    {
        // Once per change: the commands ask for it one after the other, and Ctrl+A adds row by row.
        var findings = SelectedFindings = SelectedRows
            .SelectMany(r => r switch
            {
                FindingRowViewModel f => (IEnumerable<FindingRowViewModel>)[f],
                FindingGroupViewModel g => g.Findings,
                _ => []
            })
            .Distinct().ToList();
        SelectionText = findings.Count switch
        {
            0 => "Keine Auswahl",
            1 => "1 Fundstück ausgewählt",
            _ => $"{findings.Count} Fundstücke ausgewählt, {PlanDisplay.Size(findings.Sum(f => f.Finding.Bytes))}"
        };
        foreach (var command in new IRelayCommand[]
                 {
                     AcceptCommand, RejectCommand, WrongCommand, ReopenCommand, ChangeTargetCommand, NoTargetCommand, WithdrawTargetCommand
                 })
            command.NotifyCanExecuteChanged();

        var last = SelectedRows.LastOrDefault();
        if (_detailFor is not null && last is not null && KeyOf(_detailFor) == KeyOf(last))
        {
            // The same entry again (a filter, a decision): its preview stays, its words may be new.
            _detailFor = last;
            ShowDetailTexts(last);
            return;
        }
        if (_detailFor is null && last is null) return;
        _detailFor = last;
        DetailLoad = LoadDetailAsync(last);
    }

    private void Select(FindingRowViewModel? row)
    {
        _rebuilding = true;
        SelectedRows.Clear();
        if (row is not null) SelectedRows.Add(row);
        _rebuilding = false;
        SelectionSettled();
    }

    // ------------------------------------------------------------------ deciding

    /// <summary>Kept with the decision, and the reason of a target set by hand.</summary>
    [ObservableProperty] private string _note = string.Empty;

    /// <summary>Something was decided and the list filled again; the view puts the keyboard back on the selected row.</summary>
    public event Action? Decided;

    private bool HasSelection() => SelectedFindings.Count > 0;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task AcceptAsync() => DecideAsync(FindingState.Accepted);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RejectAsync() => DecideAsync(FindingState.Rejected);

    /// <summary>The finding itself is wrong; it is kept as a test case.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task WrongAsync() => DecideAsync(FindingState.Wrong);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ReopenAsync() => DecideAsync(FindingState.Open);

    private async Task DecideAsync(FindingState state)
    {
        if (_job is not { } job || _deciding) return;
        var selected = SelectedFindings;
        if (selected.Count == 0) return;
        // A sensitive finding is decided only when it is the one thing selected, as a row of its own.
        var alone = SelectedRows is [FindingRowViewModel];
        var single = alone ? [] : selected.Where(r => r.SingleDecision).ToList();
        var change = selected.Except(single).Where(r => r.Finding.State != state).ToList();
        var label = CodeLabels.Label(state, Language);
        if (change.Count == 0)
        {
            StatusText = single.Count > 0 ? SkippedText(single.Count)
                : selected.Count == 1 ? $"Schon „{label}“." : $"Alle {selected.Count} schon „{label}“.";
            return;
        }

        var note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
        var order = Rows.OfType<FindingRowViewModel>().ToList();
        var done = 0;
        string? error = null;
        _deciding = true;
        try
        {
            foreach (var row in change)
            {
                await Task.Run(() => _findings.RecordAsync(job, row.Finding, state, ResultOrigin.Human, note));
                done++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }
        finally
        {
            _deciding = false;
        }
        if (job.Id != _job?.Id) return; // another job was opened meanwhile

        Note = string.Empty;
        foreach (var row in change) row.Refresh();
        Rebuild();
        // One after the other without the mouse: on to the next open finding below.
        if (alone && error is null && state != FindingState.Open)
        {
            var visible = Rows.OfType<FindingRowViewModel>().ToHashSet();
            var at = order.IndexOf(selected[0]);
            Select(order.Skip(at + 1).FirstOrDefault(r => r.IsOpen && visible.Contains(r))
                   ?? (visible.Contains(selected[0]) ? selected[0] : order.Take(at).LastOrDefault(visible.Contains)));
        }
        StatusText = $"{(done == 1 ? "1 Fundstück" : $"{done} Fundstücke")} „{label}“."
                     + (single.Count > 0 ? " " + SkippedText(single.Count) : string.Empty)
                     + (error is null ? string.Empty : $" Dann abgebrochen: {error}");
        Decided?.Invoke();
    }

    private static string SkippedText(int n) =>
        n == 1 ? "1 sensibles übersprungen: nur einzeln zu entscheiden." : $"{n} sensible übersprungen: nur einzeln zu entscheiden.";

    // ------------------------------------------------------------------ changing the target

    /// <summary>"Ziel ändern" while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTargetPicker))]
    private PlanTargetPickerViewModel? _targetPicker;

    public bool HasTargetPicker => TargetPicker is not null;

    private bool CanChangeTarget() => SelectedFindings.Any(r => r.CanPlace);

    /// <summary>Opens the folder tree of the libraries the job was compared with, for every selected finding that gets a place.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeTarget))]
    private async Task ChangeTargetAsync()
    {
        if (Report is not { } report || TargetPicker is not null) return;
        var rows = SelectedFindings.Where(r => r.CanPlace).ToList();
        if (rows.Count == 0) return;
        var libraries = new List<ScanJob>();
        foreach (var id in report.Inputs.LibraryJobIds ?? [])
            if (await _jobs.GetJobAsync(id) is { } library)
                libraries.Add(library);
        if (libraries.Count == 0)
        {
            StatusText = $"Der Job wurde mit keiner Bibliothek verglichen, also gibt es kein Ziel (sfo compare {report.Job.Id} --against <Bibliothek>).";
            return;
        }

        PlanTargetPickerViewModel? picker = null;
        picker = new PlanTargetPickerViewModel(
            rows.Count == 1 ? $"Ziel für {rows[0].Title}" : $"Ziel für {rows.Count} Fundstücke",
            rows.Count == 1 ? "Jetzt: " + rows[0].ActionText : string.Empty,
            rows.Count == 1 ? rows[0].Finding.Action.Reason : string.Empty,
            rows.Count - 1, libraries, _libraries,
            folder =>
            {
                var node = picker!.SelectedFolder;
                var status = node is { IsInbox: true } ? FindingTargetStatus.Inbox
                    : node is { IsNew: true } ? FindingTargetStatus.NewFolder
                    : FindingTargetStatus.ExistingFolder;
                return PlaceAsync(rows, status, folder, node?.Library.Id);
            });
        TargetPicker = picker;
        try
        {
            await picker.LoadAsync();
            await picker.Closed;
        }
        finally
        {
            TargetPicker = null;
            Decided?.Invoke();
        }
    }

    /// <summary>No target on purpose: the files stay until a person decides otherwise.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeTarget))]
    private Task NoTargetAsync() => PlaceAsync(SelectedFindings.Where(r => r.CanPlace).ToList(), FindingTargetStatus.NoTarget, null, null);

    private bool CanWithdrawTarget() => SelectedFindings.Any(r => r.IsTargetDecided);

    /// <summary>Takes a target set by hand (or by an agent) back; the finding has none until the next match.</summary>
    [RelayCommand(CanExecute = nameof(CanWithdrawTarget))]
    private Task WithdrawTargetAsync() => PlaceAsync(SelectedFindings.Where(r => r.IsTargetDecided).ToList(), null, null, null);

    /// <summary>Stores the target of the findings, all or none; null <paramref name="status"/> withdraws it. Returns why it
    /// was refused, or null.</summary>
    private async Task<string?> PlaceAsync(
        IReadOnlyList<FindingRowViewModel> rows, FindingTargetStatus? status, string? folder, long? library)
    {
        if (_job is not { } job || rows.Count == 0) return "Kein Fundstück ausgewählt.";
        var reason = string.IsNullOrWhiteSpace(Note) ? PlacedByHand : Note.Trim();
        var requests = rows.Select(r => status is { } s
            ? new PlacementRequest(r.Key, Status: s.ToCode(), LibraryJobId: library, Folder: folder, Reason: reason)
            : new PlacementRequest(r.Key, Withdraw: true)).ToList();
        try
        {
            await Task.Run(() => _placement.PlaceAsync(job.Id, [], requests));
        }
        catch (PlacementRefusedException ex)
        {
            return StatusText = "Abgewiesen: "
                + string.Join("; ", ex.Refused.SelectMany(r => r.Problems).Distinct().Select(FindingDisplay.ProblemLabel));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StatusText = ex.Message;
        }
        Note = string.Empty;
        // A target changes action, certainty and rank of a finding: computed again.
        await ReloadAsync();
        if (job.Id != _job?.Id) return null;
        var n = rows.Count == 1 ? "1 Fundstück" : $"{rows.Count} Fundstücke";
        StatusText = status is null ? $"{n}: Ziel zurückgenommen."
            : status == FindingTargetStatus.NoTarget ? $"{n}: bewusst ohne Ziel."
            : $"{n}: Ziel {_all.FirstOrDefault(r => r.Key == rows[0].Key)?.TargetText}.";
        return null;
    }

    // ------------------------------------------------------------------ detail

    [ObservableProperty] private bool _hasDetail;
    [ObservableProperty] private string _detailTitle = string.Empty;
    [ObservableProperty] private string _detailKind = string.Empty;
    [ObservableProperty] private string _detailKindColor = "#4A4F55";
    [ObservableProperty] private string _detailPath = string.Empty;
    [ObservableProperty] private string _detailFacts = string.Empty;
    [ObservableProperty] private string _detailReason = string.Empty;
    [ObservableProperty] private string _detailAction = string.Empty;
    [ObservableProperty] private string _detailActionReason = string.Empty;
    [ObservableProperty] private string _detailNotes = string.Empty;
    [ObservableProperty] private string _detailState = string.Empty;
    [ObservableProperty] private string _detailStateColor = "#A0A7B0";

    // Preview
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPictures))]
    private IReadOnlyList<FindingPicture> _pictures = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileItems))]
    private IReadOnlyList<FindingFileItem> _fileItems = [];

    [ObservableProperty] private string _fileListTitle = string.Empty;
    [ObservableProperty] private string _documentTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocumentImage))]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    private Bitmap? _documentImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocumentText))]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    private string _documentText = string.Empty;

    [ObservableProperty] private string _previewMessage = string.Empty;

    /// <summary>A sensitive entry: nothing of it is read or listed until the person asks for it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowPreviewCommand))]
    private bool _isPreviewLocked;

    public bool HasPictures => Pictures.Count > 0;
    public bool HasFileItems => FileItems.Count > 0;
    public bool HasDocumentImage => DocumentImage is not null;

    /// <summary>The text of a document, when there is no page to show.</summary>
    public bool HasDocumentText => DocumentText.Length > 0 && DocumentImage is null;

    public bool HasDocument => HasDocumentImage || DocumentText.Length > 0;

    /// <summary>The load of the shown detail; tests wait for it.</summary>
    public Task DetailLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Opens the preview of a sensitive entry, until another job is opened.</summary>
    [RelayCommand(CanExecute = nameof(IsPreviewLocked))]
    private Task ShowPreviewAsync()
    {
        _revealedKey = _detailFor is null ? null : KeyOf(_detailFor);
        return DetailLoad = LoadDetailAsync(_detailFor);
    }

    /// <summary>The folder of the finding or bundle in the file manager.</summary>
    [RelayCommand]
    private void Reveal()
    {
        var path = _detailFor switch
        {
            FindingRowViewModel r => r.Finding.Path ?? r.Finding.Directories.FirstOrDefault(),
            FindingGroupViewModel g => g.Bundle?.RootPath,
            _ => null
        };
        if (path is not null) _shell.Reveal(path);
    }

    /// <summary>A file of the list or the strip in its standard program.</summary>
    [RelayCommand]
    private void OpenFile(string path) => _shell.Open(path);

    private void ShowDetailTexts(object? row)
    {
        HasDetail = row is not null;
        switch (row)
        {
            case FindingRowViewModel r:
            {
                var f = r.Finding;
                DetailTitle = r.Title;
                DetailKind = r.KindLabel;
                DetailKindColor = r.KindColor;
                DetailPath = f.Path ?? string.Join(Environment.NewLine, f.Directories.Take(3));
                var period = r.PeriodText.Length > 0 && f.Period?.Source is { } source
                    ? $"{r.PeriodText} ({CodeLabels.Label(source, Language)})" : r.PeriodText;
                DetailFacts = string.Join(" · ", new[] { period, r.FilesText, r.SizeText, r.CertaintyLabel, r.MarkersText }.Where(t => t.Length > 0));
                DetailReason = f.Reason;
                DetailAction = r.ActionText
                               + (f.Target is not null && f.TargetDecidedBy is { } by ? $" · {CodeLabels.Label(by, Language)}" : string.Empty);
                DetailActionReason = f.Action.Reason;
                DetailNotes = string.Join(Environment.NewLine, f.TargetParts
                    .Select(p => $"{p.Label} ({PlanDisplay.Files(p.FileIds.Count)}): {p.RelativePath ?? CodeLabels.Label(p.Status, Language)}")
                    .Concat(f.Notes));
                DetailState = r.StateLabel
                              + (f.DecidedBy is { } o ? $" · {CodeLabels.Label(o, Language)}" : string.Empty)
                              + (f.DecidedAt is { } at ? $" am {at.ToLocalTime():dd.MM.yyyy HH:mm}" : string.Empty)
                              + (f.DecisionNote is { Length: > 0 } n ? $" · {n}" : string.Empty)
                              + (r is { SingleDecision: true, IsOpen: true } ? " · sensibel: nur einzeln zu entscheiden" : string.Empty);
                DetailStateColor = r.StateColor;
                break;
            }
            case FindingGroupViewModel g:
                DetailTitle = g.Title;
                DetailKind = g.KindLabel;
                DetailKindColor = "#4A4F55";
                DetailPath = g.Bundle?.RootPath ?? string.Empty;
                DetailFacts = g.Summary;
                DetailReason = g.Bundle?.Reason ?? "Fundstücke, die zu keinem Bündel gehören.";
                DetailAction = g.HasFindings
                    ? string.Join(Environment.NewLine, g.Findings.Take(8).Select(f => $"{f.KindLabel}: {f.Title} · {f.ActionText}"))
                    : "Kein Fundstück: hier ist nichts zu entscheiden.";
                DetailActionReason = string.Empty;
                DetailNotes = g.Item?.Comparison is { Count: > 0 } comparison
                    ? "Vergleich mit der Bibliothek: " + string.Join(", ", comparison.OrderBy(c => c.Key, StringComparer.Ordinal)
                        .Select(c => $"{CodeLabels.LabelOfCode<ContentState>(c.Key, Language)} {c.Value}"))
                    : string.Empty;
                DetailState = string.Empty;
                break;
            default:
                DetailTitle = DetailKind = DetailPath = DetailFacts = DetailReason = string.Empty;
                DetailAction = DetailActionReason = DetailNotes = DetailState = string.Empty;
                break;
        }
    }

    private async Task LoadDetailAsync(object? row)
    {
        _detailCts?.Cancel();
        var ct = (_detailCts = new CancellationTokenSource()).Token;
        ClearPreview();
        ShowDetailTexts(row);
        if (row is null || _job is not { } job) return;

        // Sensitive entries only after a click: no list, no picture, no text before.
        var revealed = _revealedKey == KeyOf(row);
        IsPreviewLocked = row is FindingRowViewModel { Finding.Kind: FindingKind.Sensitive } && !revealed;
        if (IsPreviewLocked)
        {
            PreviewMessage = "Sensibel: Dateien und Vorschau erst nach Klick.";
            return;
        }

        try
        {
            // Held arrow keys and Ctrl+A pass over many entries: only the one they stop at is read.
            await Task.Delay(DetailDelay, ct);
            IReadOnlyList<long> ids = row switch
            {
                FindingRowViewModel r => r.Finding.FileIds,
                FindingGroupViewModel { Bundle: { } b } => await Task.Run(() => _bundles.GetFileIdsAsync(job.Id, b.Id, ct), ct),
                FindingGroupViewModel g => g.Findings.Where(f => f.Finding.Kind != FindingKind.Sensitive)
                    .SelectMany(f => f.Finding.FileIds).Distinct().ToList(),
                _ => []
            };
            var files = await Task.Run(() => _files.GetFilesAsync(ids.Take(MaxListedFiles).ToList(), ct), ct);
            if (ct.IsCancellationRequested) return;
            FileListTitle = ids.Count == 0 ? string.Empty
                : ids.Count > files.Count ? $"{PlanDisplay.Files(ids.Count)}, {files.Count} davon:"
                : PlanDisplay.Files(ids.Count) + ":";
            FileItems = files.Select(Item).ToList();
            if (ids.Count == 0)
            {
                PreviewMessage = "Keine einzelnen Dateien in der Datenbank.";
                return;
            }
            await LoadPreviewAsync(files, revealed, ct);
        }
        catch (OperationCanceledException)
        {
            // Another entry was selected meanwhile.
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) PreviewMessage = ex.Message;
        }
    }

    private FindingFileItem Item(FileNode f)
    {
        var markers = SensitiveOutputGuard.GetMarkers(f);
        var summary = _sensitive.ShowsContent(markers) ? f.Classification?.Summary ?? string.Empty : BrowserDisplay.AccessDataHint;
        return new FindingFileItem(f.RelativePath.Replace('\\', '/'), f.FullPath,
            $"{FindingDisplay.Date(f.LastWriteTime)} · {PlanDisplay.Size(f.Size)}",
            markers == SensitiveMarker.None ? string.Empty : CodeLabels.Markers(SensitiveMarkerNames.ToTokens(markers), Language),
            summary);
    }

    /// <summary>
    /// The picture strip of photos and videos, spread over the entry, and the first page or text of its first document.
    /// A marked file is shown only in a sensitive finding the person opened; access data never.
    /// </summary>
    private async Task LoadPreviewAsync(IReadOnlyList<FileNode> files, bool revealed, CancellationToken ct)
    {
        bool Shows(FileNode f)
        {
            var markers = SensitiveOutputGuard.GetMarkers(f);
            return markers == SensitiveMarker.None || revealed && _sensitive.ShowsContent(markers);
        }

        var media = files.Where(f => f.FileType is FileType.Image or FileType.Video && Shows(f)).ToList();
        var document = files.FirstOrDefault(f => f.FileType == FileType.Document && Shows(f));
        if (media.Count == 0 && document is null)
        {
            PreviewMessage = files.Count > 0 && files.All(f => !Shows(f))
                ? files.All(f => SensitiveOutputOptions.IsAccessData(SensitiveOutputGuard.GetMarkers(f)))
                    ? BrowserDisplay.AccessDataHint : "Sensibel markiert: Vorschau nur beim sensiblen Fundstück, nach Klick."
                : "Keine Vorschau für diese Art von Dateien.";
            return;
        }

        var made = new List<FindingPicture>();
        foreach (var f in Spread(media, MaxPictures))
        {
            var image = await _preview.BuildThumbnailAsync(f, PictureWidth, ct);
            if (image is null) continue;
            if (ct.IsCancellationRequested)
            {
                // What is shown already was released with the detail; only the one not shown yet is ours.
                image.Dispose();
                return;
            }
            made.Add(new FindingPicture(image, f.Name, f.FullPath));
            Pictures = made.ToList();
        }
        if (media.Count > 0 && made.Count == 0)
            PreviewMessage = "Kein Bild lesbar; ein Doppelklick in der Dateiliste öffnet die Datei im Standardprogramm.";

        if (document is null) return;
        var preview = await _preview.BuildAsync(document, ct);
        if (ct.IsCancellationRequested)
        {
            preview.Dispose();
            return;
        }
        DocumentTitle = document.Name;
        DocumentImage = preview.ImagePreview;
        DocumentText = preview.TextPreview;
        if (preview is { HasImagePreview: false, HasTextPreview: false } && made.Count == 0)
            PreviewMessage = preview.Message;
    }

    /// <summary>At most <paramref name="count"/> of the files, spread evenly over them.</summary>
    public static IEnumerable<FileNode> Spread(IReadOnlyList<FileNode> files, int count) => files.Count <= count
        ? files
        : Enumerable.Range(0, count).Select(i => files[(int)((long)i * files.Count / count)]);

    private void ClearPreview()
    {
        var pictures = Pictures;
        var page = DocumentImage;
        Pictures = [];
        DocumentImage = null;
        foreach (var p in pictures) p.Image.Dispose();
        page?.Dispose();
        FileItems = [];
        FileListTitle = DocumentTitle = DocumentText = PreviewMessage = string.Empty;
        IsPreviewLocked = false;
    }
}
