using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Desktop.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Hosting;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// The plan of a job in the GUI (https://github.com/crack666/smart_file_organizer/issues/15): the same plan the CLI and a
/// steering agent work on, read, changed and confirmed by the person, then executed in the two steps of the CLI.
/// Every change, check and file operation is done by <see cref="PlanService"/> and <see cref="PlanExecutor"/>; this
/// class only chooses what to ask them and shows their answers.
/// </summary>
public partial class PlanViewModel : ViewModelBase
{
    private const PlanActionOrigin Person = PlanActionOrigin.Human;

    private readonly PlanService _plans;
    private readonly PlanExecutor _executor;
    private readonly ScanJobService _jobs;
    private readonly FileQueryService _files;
    private readonly IFileFactRepository _facts;
    private readonly FilePreviewService _preview;
    private readonly IShellLauncher _shell;
    private readonly SensitiveOutputOptions _sensitive;

    private bool _suppress;
    private CancellationTokenSource? _detailCts;
    private CancellationTokenSource? _runCts;
    private int _runTotal;
    private int _runDone;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    public PlanViewModel(
        PlanService plans,
        PlanExecutor executor,
        ScanJobService jobs,
        FileQueryService files,
        IFileFactRepository facts,
        FilePreviewService preview,
        IShellLauncher shell,
        SensitiveOutputOptions sensitive,
        SmartFileOrganizerPaths paths)
    {
        _plans = plans;
        _executor = executor;
        _jobs = jobs;
        _files = files;
        _facts = facts;
        _preview = preview;
        _shell = shell;
        // The GUI is used by the person at the machine itself: classification and descriptions of marked files are shown,
        // the literal text of access data never (the rule of Sensitive:Disclosure, not a second one).
        _sensitive = sensitive with { IncludeSensitive = true };
        DatabasePath = paths.DatabasePath;
    }

    // ------------------------------------------------------------------ header

    /// <summary>The database this window works on (<c>SFO_DB</c>, or the default under the application data folder).</summary>
    public string DatabasePath { get; }

    public string DatabaseText => "Datenbank: " + DatabasePath;

    public ObservableCollection<PlanJobItem> Jobs { get; } = [];
    public ObservableCollection<PlanListItem> Plans { get; } = [];
    public IReadOnlyList<PlanGrouping> Groupings { get; } = [PlanGrouping.Bundle, PlanGrouping.Folder];
    public IReadOnlyList<PlanKindOption> KindOptions { get; } = PlanDisplay.Kinds.Select(k => new PlanKindOption(k)).ToList();

    [ObservableProperty] private PlanJobItem? _selectedJob;
    [ObservableProperty] private PlanListItem? _selectedPlan;
    [ObservableProperty] private PlanGrouping _selectedGrouping = PlanGrouping.Bundle;
    [ObservableProperty] private bool _keepSource;

    /// <summary>The plan as the checks see it now.</summary>
    [ObservableProperty] private PlanState? _state;

    [ObservableProperty] private string _statusText = string.Empty;

    // ------------------------------------------------------------------ summary

    [ObservableProperty] private string _moveText = string.Empty;
    [ObservableProperty] private string _removeText = string.Empty;
    [ObservableProperty] private string _stayText = string.Empty;
    [ObservableProperty] private string _statesText = string.Empty;
    [ObservableProperty] private string _agentText = string.Empty;
    [ObservableProperty] private bool _hasAgentDecisions;
    [ObservableProperty] private string _problemsText = string.Empty;

    // ------------------------------------------------------------------ list

    /// <summary>Group headers and the rows of the expanded groups, in display order.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    public IReadOnlyList<PlanGroupViewModel> Groups { get; private set; } = [];

    public IEnumerable<PlanActionRowViewModel> AllActions => Groups.SelectMany(g => g.Actions);

    [ObservableProperty] private object? _selectedRow;
    [ObservableProperty] private PlanActionRowViewModel? _selectedAction;
    [ObservableProperty] private int _checkedCount;

    public string CheckedText => CheckedCount == 0 ? "Keine Auswahl" : $"Auswahl: {CheckedCount}";

    // ------------------------------------------------------------------ detail

    [ObservableProperty] private string _detailTitle = string.Empty;
    [ObservableProperty] private string _detailSource = string.Empty;
    [ObservableProperty] private string _detailTarget = string.Empty;
    [ObservableProperty] private string _detailRecommended = string.Empty;
    [ObservableProperty] private string _detailReason = string.Empty;
    [ObservableProperty] private string _detailNote = string.Empty;
    [ObservableProperty] private string _detailProblems = string.Empty;
    [ObservableProperty] private string _detailDecision = string.Empty;
    [ObservableProperty] private string _detailDescription = string.Empty;
    [ObservableProperty] private string _detailMarker = string.Empty;
    [ObservableProperty] private string _detailFacts = string.Empty;
    [ObservableProperty] private PlanKindOption? _selectedKind;
    [ObservableProperty] private Bitmap? _imagePreview;
    [ObservableProperty] private string _textPreview = string.Empty;
    [ObservableProperty] private string _previewMessage = string.Empty;

    public bool HasDetail => SelectedAction is not null;
    public bool HasImagePreview => ImagePreview is not null;
    public bool HasTextPreview => !string.IsNullOrWhiteSpace(TextPreview);
    public bool HasPreviewMessage => !string.IsNullOrWhiteSpace(PreviewMessage);

    // ------------------------------------------------------------------ run

    public ObservableCollection<PlanRunLine> RunLines { get; } = [];

    [ObservableProperty] private string _runTitle = string.Empty;
    [ObservableProperty] private string _runSummary = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _runProgress;
    [ObservableProperty] private string _runProgressText = string.Empty;

    /// <summary>The last report of a run or dry run, as the executor gave it.</summary>
    public PlanRunReport? LastReport { get; private set; }

    /// <summary>The confirmation waiting for the person before a step that changes files.</summary>
    [ObservableProperty] private PlanConfirmationViewModel? _confirmation;

    public bool HasConfirmation => Confirmation is not null;

    partial void OnConfirmationChanged(PlanConfirmationViewModel? value) => OnPropertyChanged(nameof(HasConfirmation));

    // ================================================================== loading

    /// <summary>Loads the source jobs of the database and opens a plan: of <paramref name="jobId"/> when given, else of
    /// the newest job that has one.</summary>
    public async Task LoadAsync(long? jobId = null, long? planId = null)
    {
        try
        {
            var all = await _jobs.GetAllJobsAsync();
            _suppress = true;
            Jobs.Clear();
            foreach (var j in all.Where(j => j.Role != ScanRole.Library))
                Jobs.Add(new PlanJobItem(j));
            _suppress = false;

            var job = jobId is { } id ? Jobs.FirstOrDefault(j => j.Job.Id == id) : null;
            if (job is null)
            {
                foreach (var candidate in Jobs)
                {
                    if ((await _plans.ListAsync(candidate.Job.Id)).Count > 0) { job = candidate; break; }
                }
            }
            await SelectJobAsync(job ?? Jobs.FirstOrDefault(), planId);
        }
        catch (Exception ex) when (ex is PlanException or JobReadException or InvalidOperationException)
        {
            StatusText = ex.Message;
        }
        finally
        {
            _suppress = false;
        }
    }

    public async Task SelectJobAsync(PlanJobItem? job, long? planId = null)
    {
        _suppress = true;
        SelectedJob = job;
        Plans.Clear();
        _suppress = false;
        if (job is null)
        {
            await SelectPlanAsync(null);
            StatusText = "Kein Quell-Job in dieser Datenbank.";
            return;
        }
        foreach (var p in await _plans.ListAsync(job.Job.Id))
            Plans.Add(new PlanListItem(p));
        var keep = await _executor.GetKeepSourceAsync(job.Job.Id);
        _suppress = true;
        KeepSource = keep;
        _suppress = false;
        await SelectPlanAsync(planId is { } id ? Plans.FirstOrDefault(p => p.Plan.Id == id) : Plans.FirstOrDefault());
        if (Plans.Count == 0)
            StatusText = $"Job {job.Job.Id} hat noch keinen Plan (sfo plan create {job.Job.Id}).";
    }

    public async Task SelectPlanAsync(PlanListItem? plan)
    {
        _suppress = true;
        SelectedPlan = plan;
        _suppress = false;
        RunLines.Clear();
        RunTitle = RunSummary = string.Empty;
        LastReport = null;
        if (plan is null)
        {
            Apply(null);
            return;
        }
        await ReloadAsync();
        StatusText = $"Plan {plan.Plan.Id} geöffnet.";
    }

    /// <summary>Reads the plan again and checks it as it stands now.</summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        if (SelectedPlan is not { } p) return;
        try
        {
            Apply(await _plans.GetAsync(p.Plan.Id));
        }
        catch (Exception ex) when (ex is PlanException or JobReadException)
        {
            StatusText = ex.Message;
        }
    }

    partial void OnSelectedJobChanged(PlanJobItem? value)
    {
        if (!_suppress) _ = SelectJobAsync(value);
    }

    partial void OnSelectedPlanChanged(PlanListItem? value)
    {
        if (!_suppress) _ = SelectPlanAsync(value);
    }

    partial void OnSelectedGroupingChanged(PlanGrouping value) => Apply(State);

    partial void OnKeepSourceChanged(bool value)
    {
        if (_suppress || SelectedJob is not { } job) return;
        _ = SetKeepSourceAsync(job.Job.Id, value);
    }

    private async Task SetKeepSourceAsync(long jobId, bool keep)
    {
        await _executor.SetKeepSourceAsync(jobId, keep);
        StatusText = keep
            ? "Quelle bleibt unberührt: Umziehen kopiert nur, nichts wird entfernt."
            : "Quelle darf wieder entfernt werden (Schritt 2).";
    }

    // ================================================================== list

    private void Apply(PlanState? state)
    {
        var selectedId = SelectedAction?.Id;
        var checkedIds = AllActions.Where(r => r.IsChecked).Select(r => r.Id).ToHashSet();
        State = state;

        foreach (var r in AllActions)
            r.PropertyChanged -= OnRowPropertyChanged;

        var roots = state?.Libraries.Select(l => l.RootPath).ToList() ?? [];
        var rows = (state?.Actions ?? []).Select(s => new PlanActionRowViewModel(s, roots) { IsChecked = checkedIds.Contains(s.Action.Id) }).ToList();
        foreach (var r in rows)
            r.PropertyChanged += OnRowPropertyChanged;

        Groups = SelectedGrouping == PlanGrouping.Folder
            ? rows.GroupBy(r => r.RelativeFolder, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new PlanGroupViewModel("folder:" + g.Key, g.Key == "." ? "(Job-Ordner)" : g.Key,
                    state!.Job.RootPath, g.ToList()))
                .ToList()
            : rows.GroupBy(r => r.Action.FindingKey, StringComparer.Ordinal)
                .OrderByDescending(g => g.Sum(r => r.Action.Bytes)).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new PlanGroupViewModel("bundle:" + g.Key, g.First().Action.Unit,
                    g.First().Action.FindingKind.ToCode(), g.ToList()))
                .ToList();
        foreach (var g in Groups)
            g.IsExpanded = !_collapsed.Contains(g.Key);

        RebuildRows();
        UpdateSummary();
        UpdateChecked();
        OnPropertyChanged(nameof(AllActions));

        var again = selectedId is { } id ? rows.FirstOrDefault(r => r.Id == id) : null;
        SelectedRow = again;
        if (again is null)
            SelectedAction = null;
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var g in Groups)
        {
            Rows.Add(g);
            if (!g.IsExpanded) continue;
            foreach (var r in g.Actions)
                Rows.Add(r);
        }
    }

    private void UpdateSummary()
    {
        var actions = State?.Actions.Select(s => s.Action).ToList() ?? [];
        var live = actions.Where(a => a.State != PlanActionState.Rejected).ToList();
        string Tally(IEnumerable<PlanAction> of)
        {
            var list = of.ToList();
            return $"{list.Count} · {PlanDisplay.Files(list.Sum(a => a.Files))} · {PlanDisplay.Size(list.Sum(a => a.Bytes))}";
        }
        MoveText = "Zieht um: " + Tally(live.Where(a => a.Kind is PlanActionKind.Move or PlanActionKind.Copy));
        RemoveText = "Wird entfernt: " + Tally(live.Where(a => a.Kind is PlanActionKind.Quarantine or PlanActionKind.DeleteSource));
        StayText = "Bleibt: " + Tally(actions.Where(a => a.Kind == PlanActionKind.Leave || a.State == PlanActionState.Rejected));
        StatesText = string.Join(" · ", Enum.GetValues<PlanActionState>()
            .Select(s => (s, n: actions.Count(a => a.State == s)))
            .Where(x => x.n > 0)
            .Select(x => $"{PlanDisplay.StateLabel(x.s)} {x.n}"));
        var byAgent = actions.Count(a => a.DecidedBy == PlanActionOrigin.Agent && a.State is PlanActionState.Approved or PlanActionState.Rejected);
        HasAgentDecisions = byAgent > 0;
        AgentText = byAgent > 0 ? $"Vom Agenten entschieden: {byAgent}" : string.Empty;
        var problems = AllActions.Count(r => r.HasProblems);
        ProblemsText = problems > 0 ? $"Mit Problemen: {problems}" : string.Empty;
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanActionRowViewModel.IsChecked))
            UpdateChecked();
    }

    private void UpdateChecked()
    {
        CheckedCount = AllActions.Count(r => r.IsChecked);
        OnPropertyChanged(nameof(CheckedText));
    }

    partial void OnSelectedRowChanged(object? value)
    {
        if (value is PlanActionRowViewModel row)
            SelectedAction = row;
    }

    partial void OnSelectedActionChanged(PlanActionRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasDetail));
        _ = LoadDetailAsync(value);
    }

    [RelayCommand]
    private void ToggleGroup(PlanGroupViewModel group)
    {
        group.IsExpanded = !group.IsExpanded;
        if (group.IsExpanded) _collapsed.Remove(group.Key);
        else _collapsed.Add(group.Key);
        RebuildRows();
    }

    [RelayCommand]
    private void CheckGroup(PlanGroupViewModel group)
    {
        var all = group.Actions.All(r => r.IsChecked);
        foreach (var r in group.Actions)
            r.IsChecked = !all;
    }

    [RelayCommand]
    private void ClearChecks()
    {
        foreach (var r in AllActions)
            r.IsChecked = false;
    }

    private IReadOnlyList<long> CheckedIds() => AllActions.Where(r => r.IsChecked).Select(r => r.Id).ToList();

    /// <summary>One action named is a decision on its own; several are a selection, which passes over sensitive ones.</summary>
    private static PlanSelection Selection(IReadOnlyList<long> ids) => new(ids, AsBatch: ids.Count > 1);

    // ================================================================== decide

    [RelayCommand] private Task ApproveSelectedAsync() => DecideAsync(SelectedAction is { } r ? new PlanSelection([r.Id]) : null, approve: true);
    [RelayCommand] private Task RejectSelectedAsync() => DecideAsync(SelectedAction is { } r ? new PlanSelection([r.Id]) : null, approve: false);
    [RelayCommand] private Task ApproveCheckedAsync() => DecideAsync(CheckedSelection(), approve: true);
    [RelayCommand] private Task RejectCheckedAsync() => DecideAsync(CheckedSelection(), approve: false);
    [RelayCommand] private Task ApproveGroupAsync(PlanGroupViewModel group) => DecideAsync(new PlanSelection(group.Actions.Select(r => r.Id).ToList(), AsBatch: true), approve: true);
    [RelayCommand] private Task RejectGroupAsync(PlanGroupViewModel group) => DecideAsync(new PlanSelection(group.Actions.Select(r => r.Id).ToList(), AsBatch: true), approve: false);
    [RelayCommand] private Task ApproveAllAsync() => DecideAsync(new PlanSelection([], All: true), approve: true);
    [RelayCommand] private Task RejectAllAsync() => DecideAsync(new PlanSelection([], All: true), approve: false);

    private PlanSelection? CheckedSelection() => CheckedIds() is { Count: > 0 } ids ? Selection(ids) : null;

    private async Task DecideAsync(PlanSelection? selection, bool approve)
    {
        if (SelectedPlan is not { } p || selection is null) return;
        try
        {
            var result = await _plans.DecideAsync(p.Plan.Id, selection, approve, Person);
            Apply(result.State);
            StatusText = $"{result.Changed.Count} {(approve ? "bestätigt" : "abgelehnt")}" + Skipped(result.Skipped);
        }
        catch (Exception ex) when (ex is PlanException or JobReadException)
        {
            StatusText = ex.Message;
        }
    }

    /// <summary>Sets what a steering agent confirmed or rejected back to proposed, so the person decides it.</summary>
    [RelayCommand]
    private async Task ResetAgentDecisionsAsync()
    {
        if (SelectedPlan is not { } p) return;
        var result = await _plans.ResetDecisionsAsync(p.Plan.Id, new PlanSelection([], All: true), PlanActionOrigin.Agent);
        Apply(result.State);
        StatusText = $"{result.Changed.Count} Entscheidungen des Agenten auf „Vorgeschlagen“ zurückgesetzt.";
    }

    /// <summary>Sets the ticked actions (or the selected one) back to proposed, whoever decided them.</summary>
    [RelayCommand]
    private async Task ResetCheckedAsync()
    {
        if (SelectedPlan is not { } p) return;
        var ids = CheckedIds() is { Count: > 0 } c ? c : SelectedAction is { } r ? [r.Id] : [];
        if (ids.Count == 0) return;
        var result = await _plans.ResetDecisionsAsync(p.Plan.Id, new PlanSelection(ids), decidedBy: null);
        Apply(result.State);
        StatusText = $"{result.Changed.Count} auf „Vorgeschlagen“ zurückgesetzt" + Skipped(result.Skipped);
    }

    private static string Skipped(IReadOnlyList<SkippedAction> skipped) => skipped.Count == 0
        ? "."
        : $", {skipped.Count} übersprungen ({string.Join(", ", skipped.SelectMany(s => s.Reasons).GroupBy(r => r).Select(g => $"{g.Key}: {g.Count()}"))}).";

    // ================================================================== kind

    [RelayCommand] private Task MarkSelectedForRemovalAsync() => RetypeAsync(SelectedAction is { } r ? [r.Id] : [], kind: null);
    [RelayCommand] private Task MarkCheckedForRemovalAsync() => RetypeAsync(CheckedIds(), kind: null);
    [RelayCommand] private Task LeaveSelectedAsync() => RetypeAsync(SelectedAction is { } r ? [r.Id] : [], PlanActionKind.Leave);
    [RelayCommand] private Task LeaveCheckedAsync() => RetypeAsync(CheckedIds(), PlanActionKind.Leave);
    [RelayCommand] private Task MoveCheckedAsync() => RetypeAsync(CheckedIds(), PlanActionKind.Move);

    /// <summary>Sets the kind of the selected action through the switch in the detail panel.</summary>
    public Task SetSelectedKindAsync(PlanActionKind kind) =>
        RetypeAsync(SelectedAction is { } r ? [r.Id] : [], kind);

    partial void OnSelectedKindChanged(PlanKindOption? value)
    {
        if (_suppress || value is null || SelectedAction is not { } row || row.Action.Kind == value.Kind) return;
        _ = SetSelectedKindAsync(value.Kind);
    }

    /// <summary>A new kind for the actions (null: remove as clutter), with the target the plan service gives it.</summary>
    private async Task RetypeAsync(IReadOnlyList<long> ids, PlanActionKind? kind)
    {
        if (SelectedPlan is not { } p || ids.Count == 0) return;
        try
        {
            var result = kind is { } k
                ? await _plans.SetKindAsync(p.Plan.Id, ids, k, Person)
                : await _plans.MarkForRemovalAsync(p.Plan.Id, ids, Person);
            Apply(result.State);
            var what = kind is { } w ? PlanDisplay.KindLabel(w) : "Entfernen";
            StatusText = $"{result.Changed.Count} auf „{what}“ gestellt (neu zu bestätigen)"
                         + (result.Refused.Count == 0 ? "." : $"; abgewiesen: {string.Join("; ", result.Refused.Select(r => r.Message))}");
        }
        catch (Exception ex) when (ex is PlanException or JobReadException)
        {
            StatusText = ex.Message;
            Apply(State);
        }
    }

    // ================================================================== detail

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedAction is { } r) _shell.Open(r.Action.SourcePath);
    }

    [RelayCommand]
    private void RevealSelected()
    {
        if (SelectedAction is { } r) _shell.Reveal(r.Action.SourcePath);
    }

    [RelayCommand]
    private void RevealTarget()
    {
        if (SelectedAction?.Action.TargetPath is { } t)
            _shell.Reveal(File.Exists(t) || Directory.Exists(t) ? t : Path.GetDirectoryName(t) ?? t);
    }

    private async Task LoadDetailAsync(PlanActionRowViewModel? row)
    {
        _detailCts?.Cancel();
        _detailCts = new CancellationTokenSource();
        var ct = _detailCts.Token;

        _suppress = true;
        SelectedKind = row is null ? null : KindOptions.First(o => o.Kind == row.Action.Kind);
        _suppress = false;
        SetPreview(null, string.Empty, string.Empty);
        if (row is null)
        {
            DetailTitle = DetailSource = DetailTarget = DetailRecommended = DetailReason = DetailNote = string.Empty;
            DetailProblems = DetailDecision = DetailDescription = DetailMarker = DetailFacts = string.Empty;
            return;
        }

        var a = row.Action;
        DetailTitle = row.Name;
        DetailSource = a.SourcePath;
        DetailTarget = a.TargetPath ?? "kein Ziel";
        DetailRecommended = $"Empfohlen: {PlanDisplay.KindLabel(a.Recommended)}" + (a.Kind != a.Recommended ? " (geändert)" : string.Empty);
        DetailReason = a.Reason;
        DetailNote = a.Note ?? string.Empty;
        DetailProblems = row.HasProblems ? "Probleme: " + row.ProblemsText : string.Empty;
        DetailDecision = $"{PlanDisplay.StateLabel(a.State)} · Herkunft {PlanDisplay.OriginLabel(a.Origin)}"
                         + (a.DecidedBy is { } by ? $" · entschieden von {PlanDisplay.OriginLabel(by)}" : string.Empty)
                         + (a.DecidedAt is { } at ? $" am {at.ToLocalTime():dd.MM.yyyy HH:mm}" : string.Empty);
        DetailDescription = DetailMarker = DetailFacts = string.Empty;

        try
        {
            if (a.SourceKind == PlanSourceKind.File && a.FileNodeId is { } fileId)
                await LoadFileDetailAsync(fileId, ct);
            else
                await LoadFolderDetailAsync(a, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadFileDetailAsync(long fileId, CancellationToken ct)
    {
        var file = await _files.GetFileByIdAsync(fileId, ct);
        if (ct.IsCancellationRequested) return;
        if (file is null)
        {
            SetPreview(null, string.Empty, "Die Datei ist nicht mehr in der Datenbank.");
            return;
        }

        var shows = _sensitive.ShowsContent(file.SensitiveMarkers);
        var markers = SensitiveMarkerNames.ToTokens(file.SensitiveMarkers);
        DetailMarker = markers.Count > 0 ? "Merkmal: " + string.Join(", ", markers) : string.Empty;
        DetailDescription = file.Classification is { } c
            ? $"{c.Category}" + (shows && !string.IsNullOrWhiteSpace(c.Summary) ? " · " + c.Summary : string.Empty)
            : string.Empty;
        var facts = await _facts.GetByFileNodeIdAsync(fileId, ct);
        DetailFacts = string.Join(Environment.NewLine, new[]
            {
                $"{file.FileType} · {PlanDisplay.Size(file.Size)} · geändert {file.LastWriteTime:dd.MM.yyyy HH:mm}"
            }
            .Concat(facts.Where(f => shows || !f.Key.IsFreeText()).Select(f => $"{f.Key.ToCode()}: {f.Value}")));

        if (!shows)
        {
            SetPreview(null, string.Empty, "Inhalt wird nicht angezeigt: Zugangsdaten. „Öffnen“ zeigt die Datei im Standardprogramm.");
            return;
        }
        var preview = await _preview.BuildAsync(file, ct);
        if (ct.IsCancellationRequested) { preview.ImagePreview?.Dispose(); return; }
        SetPreview(preview.ImagePreview, preview.TextPreview, preview.Message);
    }

    private async Task LoadFolderDetailAsync(PlanAction a, CancellationToken ct)
    {
        if (State is not { } s) return;
        var files = await _files.GetFilesInDirectoryAsync(s.Job.Id, a.SourcePath, ct);
        if (ct.IsCancellationRequested) return;
        DetailFacts = $"Ordner · {PlanDisplay.Files(a.Files)} · {PlanDisplay.Size(a.Bytes)}" + Environment.NewLine
                      + string.Join(Environment.NewLine, files.Take(12).Select(f => "  " + f.Name))
                      + (files.Count > 12 ? $"{Environment.NewLine}  … und {files.Count - 12} weitere" : string.Empty);
        if (files.Any(f => f.SensitiveMarkers != SensitiveMarker.None))
            DetailMarker = "Merkmal: enthält markierte Dateien";
        var first = files.FirstOrDefault(f => f.FileType == FileType.Image && _sensitive.ShowsContent(f.SensitiveMarkers))
                    ?? files.FirstOrDefault(f => _sensitive.ShowsContent(f.SensitiveMarkers));
        if (first is null)
        {
            SetPreview(null, string.Empty, "Keine Vorschau für diesen Ordner.");
            return;
        }
        DetailDescription = $"Vorschau: {first.Name}" + (first.Classification?.Summary is { Length: > 0 } sum ? " · " + sum : string.Empty);
        var preview = await _preview.BuildAsync(first, ct);
        if (ct.IsCancellationRequested) { preview.ImagePreview?.Dispose(); return; }
        SetPreview(preview.ImagePreview, preview.TextPreview, preview.Message);
    }

    private void SetPreview(Bitmap? image, string text, string message)
    {
        if (!ReferenceEquals(ImagePreview, image)) ImagePreview?.Dispose();
        ImagePreview = image;
        TextPreview = text;
        PreviewMessage = message;
        OnPropertyChanged(nameof(HasImagePreview));
        OnPropertyChanged(nameof(HasTextPreview));
        OnPropertyChanged(nameof(HasPreviewMessage));
    }

    // ================================================================== run

    /// <summary>Step 1 as a dry run: what would be copied and checked, and what would be passed over. Changes nothing.</summary>
    [RelayCommand]
    public async Task DryRunAsync()
    {
        if (SelectedPlan is not { } p || IsRunning) return;
        var report = await _executor.ApplyAsync(p.Plan.Id, new PlanRunOptions([], Execute: false));
        ShowReport("Trockenlauf · Schritt 1 (kopieren und prüfen)", report);
        StatusText = "Trockenlauf: nichts verändert.";
    }

    /// <summary>Step 1: copy and check every confirmed action, after the person confirmed the dry run's summary.</summary>
    [RelayCommand]
    public async Task ApplyAsync()
    {
        if (SelectedPlan is not { } p || IsRunning) return;
        var dry = await _executor.ApplyAsync(p.Plan.Id, new PlanRunOptions([], Execute: false));
        ShowReport("Trockenlauf · Schritt 1 (kopieren und prüfen)", dry);
        var run = dry.Items.Where(i => i.Outcome == "would_run").ToList();
        if (run.Count == 0)
        {
            StatusText = "Schritt 1: nichts zu tun (keine bestätigte Aktion ohne Problem).";
            return;
        }
        var lines = new List<string>
        {
            $"{run.Count} Aktionen, {PlanDisplay.Files(run.Sum(i => i.Files))}, {PlanDisplay.Size(run.Sum(i => i.Bytes))} werden in die Bibliothek kopiert und per voller Prüfsumme geprüft.",
            "Kein Original wird in diesem Schritt angefasst.",
        };
        var skipped = dry.Items.Count - run.Count;
        if (skipped > 0) lines.Add($"{skipped} Aktionen werden übersprungen (siehe Liste unten).");
        if (dry.KeepSource) lines.Add("Quelle bleibt unberührt (keep_source): Umziehen kopiert nur.");
        if (!await ConfirmAsync("Schritt 1 ausführen?", lines, "Kopieren und prüfen", isDanger: false)) return;
        await RunAsync("Schritt 1 · kopieren und prüfen", run.Count,
            (o, ct) => _executor.ApplyAsync(p.Plan.Id, o, ct));
    }

    /// <summary>Step 2: the checked originals go into the hold folder, after a confirmation with what goes and the room freed.</summary>
    [RelayCommand]
    public async Task RemoveSourcesAsync()
    {
        if (SelectedPlan is not { } p || IsRunning) return;
        var dry = await _executor.RemoveSourcesAsync(p.Plan.Id, new PlanRunOptions([], Execute: false));
        ShowReport("Trockenlauf · Schritt 2 (Originale entfernen)", dry);
        var run = dry.Items.Where(i => i.Outcome == "would_run").ToList();
        if (run.Count == 0)
        {
            StatusText = "Schritt 2: nichts zu tun (kein Original wartet; erst Schritt 1 ausführen).";
            return;
        }
        var lines = new List<string>
        {
            $"Die Originale von {run.Count} Aktionen ({PlanDisplay.Files(run.Sum(i => i.Files))}) werden unmittelbar vorher noch einmal mit der Kopie verglichen "
            + $"und dann in den Rückhalteordner {PlanExecutor.HoldFolderName} im Job-Ordner verschoben.",
            $"Frei wird: {PlanDisplay.Size(run.Sum(i => i.Bytes))}.",
            "„Zurücknehmen“ legt sie zurück. Endgültig gelöscht wird nichts (das kann nur sfo plan purge).",
        };
        if (!await ConfirmAsync("Schritt 2: Originale entfernen?", lines, "Originale entfernen", isDanger: true)) return;
        await RunAsync("Schritt 2 · Originale entfernen", run.Count,
            (o, ct) => _executor.RemoveSourcesAsync(p.Plan.Id, o, ct));
    }

    /// <summary>Takes back what the plan did, newest first, after a confirmation.</summary>
    [RelayCommand]
    public async Task UndoAsync()
    {
        if (SelectedPlan is not { } p || IsRunning) return;
        var dry = await _executor.UndoAsync(p.Plan.Id, new PlanRunOptions([], Execute: false));
        ShowReport("Trockenlauf · Zurücknehmen", dry);
        var run = dry.Items.Where(i => i.Outcome == "would_run").ToList();
        if (run.Count == 0)
        {
            StatusText = "Zurücknehmen: nichts zu tun.";
            return;
        }
        var lines = new List<string>
        {
            $"{run.Count} Aktionen werden zurückgenommen: Originale zurück an ihren Platz, dann die Kopien dieses Plans entfernt, "
            + "aber nur, wo das Original mit seiner Prüfsumme zurück ist und die Kopie unverändert.",
        };
        if (!await ConfirmAsync("Zurücknehmen?", lines, "Zurücknehmen", isDanger: false)) return;
        await RunAsync("Zurücknehmen", run.Count, (o, ct) => _executor.UndoAsync(p.Plan.Id, o, ct));
    }

    /// <summary>Stops a running step; the executor treats it like Ctrl+C and the next run settles the open step.</summary>
    [RelayCommand]
    private void CancelRun() => _runCts?.Cancel();

    private async Task<bool> ConfirmAsync(string title, IReadOnlyList<string> lines, string confirmText, bool isDanger)
    {
        var confirmation = new PlanConfirmationViewModel(title, lines, confirmText, isDanger);
        Confirmation = confirmation;
        try
        {
            return await confirmation.Answer;
        }
        finally
        {
            Confirmation = null;
        }
    }

    private async Task RunAsync(string title, int total, Func<PlanRunOptions, CancellationToken, Task<PlanRunReport>> run)
    {
        _runCts = new CancellationTokenSource();
        _runTotal = Math.Max(1, total);
        _runDone = 0;
        IsRunning = true;
        RunProgress = 0;
        RunProgressText = $"0 / {total}";
        var names = AllActions.ToDictionary(r => r.Id, r => r.Name);
        var progress = new Progress<PlanRunItem>(item =>
        {
            _runDone++;
            RunProgress = 100.0 * Math.Min(_runDone, _runTotal) / _runTotal;
            RunProgressText = $"{_runDone} / {_runTotal} · {(names.TryGetValue(item.ActionId, out var n) ? n : item.ActionId.ToString())}: {item.Outcome}";
        });
        var options = new PlanRunOptions([], Execute: true) { Progress = progress };
        var ct = _runCts.Token;
        try
        {
            var report = await Task.Run(() => run(options, ct), ct);
            RunProgress = 100;
            RunProgressText = $"{report.Items.Count} / {total}";
            ShowReport(title, report);
            var incomplete = report.Items.Count(i => i.Outcome is "failed" or "partial");
            StatusText = incomplete == 0 ? $"{title}: fertig." : $"{title}: {incomplete} Aktionen nicht vollständig, siehe Liste.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"{title}: abgebrochen. Der nächste Lauf schließt den offenen Schritt ab oder nimmt ihn zurück.";
        }
        catch (Exception ex) when (ex is PlanException or JobReadException or IOException)
        {
            StatusText = $"{title}: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _runCts.Dispose();
            _runCts = null;
            await ReloadAsync();
        }
    }

    private void ShowReport(string title, PlanRunReport report)
    {
        LastReport = report;
        var names = AllActions.ToDictionary(r => r.Id, r => r.Name);
        RunLines.Clear();
        foreach (var i in report.Items.OrderBy(i => i.Outcome is "done" or "would_run").ThenBy(i => i.ActionId))
        {
            RunLines.Add(new PlanRunLine(i.ActionId, names.TryGetValue(i.ActionId, out var n) ? n : $"Aktion {i.ActionId}",
                i.Outcome, string.Join(", ", i.Reasons), i.Detail ?? string.Empty,
                IsProblem: i.Outcome is "failed" or "partial" or "skipped" or "would_fail"));
        }
        var runs = report.Items.Where(i => i.Outcome is "would_run" or "done").ToList();
        var collisions = report.Items.Count(i => i.Reasons.Any(r => r is "target_collision" or "name_collision" or "already_present"));
        RunTitle = title + (report.Executed ? string.Empty : " – nichts verändert");
        RunSummary = $"{runs.Count} {(report.Executed ? "ausgeführt" : "würden laufen")} · {PlanDisplay.Files(runs.Sum(i => i.Files))} · "
                     + $"{PlanDisplay.Size(runs.Sum(i => i.Bytes))} · übersprungen {report.Items.Count(i => i.Outcome == "skipped")} · "
                     + $"fehlgeschlagen {report.Items.Count(i => i.Outcome is "failed" or "would_fail")} · Kollisionen {collisions}"
                     + (report.Removal.Actions > 0
                         ? $" · warten auf Schritt 2: {report.Removal.Actions} ({PlanDisplay.Size(report.Removal.Bytes)})"
                         : string.Empty);
    }
}
