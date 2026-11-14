using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Library;
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
/// steering agent work on, read, changed and released by the person, then executed in the two steps of the CLI.
/// Every change, check and file operation is done by <see cref="PlanService"/> and <see cref="PlanExecutor"/>; this
/// class only chooses what to ask them and shows their answers.
/// <para>
/// Since https://github.com/crack666/smart_file_organizer/issues/102 the person answers one question per action, what
/// happens to the file (<see cref="PlanOutcome"/>: Umziehen, In den Papierkorb, Hier lassen), and then releases
/// (<c>approved</c>) all, a group or a selection at once. The view never rejects: "Hier lassen" sets the kind
/// <c>leave</c>. A rejected action (from the CLI or an agent) shows as "Hier lassen" with the state "Abgelehnt"; a
/// release passes over it, and choosing an outcome for it decides it anew.
/// </para>
/// </summary>
public partial class PlanViewModel : ViewModelBase
{
    private const PlanActionOrigin Person = PlanActionOrigin.Human;

    private readonly PlanService _plans;
    private readonly PlanExecutor _executor;
    private readonly LibraryTargetService _libraries;
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
        LibraryTargetService libraries,
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
        _libraries = libraries;
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
    [ObservableProperty] private string _binText = string.Empty;
    [ObservableProperty] private string _stayText = string.Empty;
    [ObservableProperty] private string _releasedText = string.Empty;
    [ObservableProperty] private string _statesText = string.Empty;

    public string MoveColor => PlanDisplay.OutcomeTile(PlanOutcome.Move);
    public string BinColor => PlanDisplay.OutcomeTile(PlanOutcome.Bin);
    public string StayColor => PlanDisplay.OutcomeTile(PlanOutcome.Stay);
    public string MoveHint => PlanDisplay.OutcomeHint(PlanOutcome.Move);
    public string BinHint => PlanDisplay.OutcomeHint(PlanOutcome.Bin);
    public string StayHint => PlanDisplay.OutcomeHint(PlanOutcome.Stay);
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
    [ObservableProperty] private string _detailMedia = string.Empty;
    [ObservableProperty] private string _detailConsequence = string.Empty;
    [ObservableProperty] private string _detailOutcomeColor = PlanDisplay.OutcomeColor(PlanOutcome.Stay);
    [ObservableProperty] private string _detailRelease = string.Empty;
    [ObservableProperty] private string _detailReleaseColor = PlanDisplay.StateColor(PlanActionState.Proposed);
    [ObservableProperty] private IReadOnlyList<PlanOutcomeChoice> _outcomeChoices = [];
    [ObservableProperty] private PlanKindOption? _selectedKind;
    [ObservableProperty] private Bitmap? _imagePreview;
    [ObservableProperty] private string _textPreview = string.Empty;
    [ObservableProperty] private string _previewMessage = string.Empty;

    public bool HasDetail => SelectedAction is not null;
    public bool HasImagePreview => ImagePreview is not null;
    public bool HasTextPreview => !string.IsNullOrWhiteSpace(TextPreview);
    public bool HasPreviewMessage => !string.IsNullOrWhiteSpace(PreviewMessage);
    public bool HasMedia => !string.IsNullOrWhiteSpace(DetailMedia);

    partial void OnDetailMediaChanged(string value) => OnPropertyChanged(nameof(HasMedia));

    // ------------------------------------------------------------------ target picker

    /// <summary>"Ziel ändern" while it is open.</summary>
    [ObservableProperty] private PlanTargetPickerViewModel? _targetPicker;

    public bool HasTargetPicker => TargetPicker is not null;

    partial void OnTargetPickerChanged(PlanTargetPickerViewModel? value) => OnPropertyChanged(nameof(HasTargetPicker));

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
        // The lines and the detail say what executing does, which depends on the switch.
        Apply(State);
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
        var rows = (state?.Actions ?? [])
            .Select(s => new PlanActionRowViewModel(s, roots, KeepSource) { IsChecked = checkedIds.Contains(s.Action.Id) })
            .ToList();
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
        string Tally(PlanOutcome outcome)
        {
            var list = actions.Where(a => PlanDisplay.OutcomeOf(a) == outcome).ToList();
            return $"{PlanDisplay.OutcomeLabel(outcome)}: {list.Count} · {PlanDisplay.Files(list.Sum(a => a.Files))} · {PlanDisplay.Size(list.Sum(a => a.Bytes))}";
        }
        MoveText = Tally(PlanOutcome.Move);
        BinText = Tally(PlanOutcome.Bin);
        StayText = Tally(PlanOutcome.Stay);
        var open = actions.Where(a => a.State is PlanActionState.Proposed or PlanActionState.Approved).ToList();
        ReleasedText = open.Count == 0 ? string.Empty : $"Freigegeben: {open.Count(a => a.State == PlanActionState.Approved)} von {open.Count}";
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


    private IReadOnlyList<PlanActionRowViewModel> CheckedRows() => AllActions.Where(r => r.IsChecked).ToList();

    private IReadOnlyList<PlanActionRowViewModel> SelectedRows() => SelectedAction is { } r ? [r] : [];

    // ================================================================== release

    // One action named is released on its own (the only way for a sensitive one); several are a selection, which passes
    // over sensitive actions as `sfo plan approve --all` does. A rejected action is never released: it shows as
    // "Hier lassen", and a release must not make it move. Choosing an outcome for it decides it anew.

    [RelayCommand] private Task ReleaseSelectedAsync() => ReleaseAsync(SelectedRows(), asBatch: false);
    [RelayCommand] private Task ReleaseCheckedAsync() => ReleaseAsync(CheckedRows(), asBatch: CheckedCount > 1);
    [RelayCommand] private Task ReleaseGroupAsync(PlanGroupViewModel group) => ReleaseAsync(group.Actions, asBatch: true);
    [RelayCommand] private Task ReleaseAllAsync() => ReleaseAsync(AllActions.ToList(), asBatch: true);

    private async Task ReleaseAsync(IReadOnlyList<PlanActionRowViewModel> rows, bool asBatch)
    {
        if (SelectedPlan is not { } p || rows.Count == 0) return;
        var rejected = rows.Count(r => r.Action.State == PlanActionState.Rejected);
        var ids = rows.Where(r => r.Action.State != PlanActionState.Rejected).Select(r => r.Id).ToList();
        if (ids.Count == 0)
        {
            StatusText = $"Nichts freigegeben: {rejected} abgelehnt; erst einen Ausgang wählen.";
            return;
        }
        try
        {
            var result = await _plans.DecideAsync(p.Plan.Id, new PlanSelection(ids, AsBatch: asBatch), approve: true, Person);
            Apply(result.State);
            StatusText = $"{result.Changed.Count} freigegeben" + Skipped(result.Skipped, rejected);
        }
        catch (Exception ex) when (ex is PlanException or JobReadException)
        {
            StatusText = ex.Message;
        }
    }

    /// <summary>Takes the release of the ticked actions (or the selected one) back: they are open again.</summary>
    [RelayCommand]
    private async Task RevokeReleaseAsync()
    {
        if (SelectedPlan is not { } p) return;
        var rows = CheckedCount > 0 ? CheckedRows() : SelectedRows();
        // Only a release is taken back; a rejection is decided anew by choosing an outcome.
        var ids = rows.Where(r => r.Action.State == PlanActionState.Approved).Select(r => r.Id).ToList();
        if (ids.Count == 0)
        {
            StatusText = "Nichts freigegeben in der Auswahl.";
            return;
        }
        var result = await _plans.ResetDecisionsAsync(p.Plan.Id, new PlanSelection(ids), decidedBy: null);
        Apply(result.State);
        StatusText = $"Freigabe von {result.Changed.Count} zurückgenommen" + Skipped(result.Skipped, 0);
    }

    /// <summary>Sets what a steering agent released or rejected back to open, so the person decides it.</summary>
    [RelayCommand]
    private async Task ResetAgentDecisionsAsync()
    {
        if (SelectedPlan is not { } p) return;
        var result = await _plans.ResetDecisionsAsync(p.Plan.Id, new PlanSelection([], All: true), PlanActionOrigin.Agent);
        Apply(result.State);
        StatusText = $"{result.Changed.Count} Entscheidungen des Agenten auf „Offen“ zurückgesetzt.";
    }

    private static string Skipped(IReadOnlyList<SkippedAction> skipped, int rejected)
    {
        var parts = skipped.SelectMany(s => s.Reasons).GroupBy(r => r).Select(g => $"{g.Key}: {g.Count()}").ToList();
        if (rejected > 0) parts.Add($"abgelehnt: {rejected}");
        var count = skipped.Count + rejected;
        return count == 0 ? "." : $", {count} übersprungen ({string.Join(", ", parts)}).";
    }

    // ================================================================== outcome

    [RelayCommand] private Task SetSelectedOutcomeAsync(PlanOutcome outcome) => SetOutcomeAsync(SelectedRows(), outcome);
    [RelayCommand] private Task MoveCheckedAsync() => SetOutcomeAsync(CheckedRows(), PlanOutcome.Move);
    [RelayCommand] private Task BinCheckedAsync() => SetOutcomeAsync(CheckedRows(), PlanOutcome.Bin);
    [RelayCommand] private Task StayCheckedAsync() => SetOutcomeAsync(CheckedRows(), PlanOutcome.Stay);

    /// <summary>
    /// What happens to the files of <paramref name="rows"/>: <see cref="PlanOutcome.Move"/> sets <c>move</c> (out of
    /// quarantine with its old target), <see cref="PlanOutcome.Bin"/> asks the plan for <c>delete_source</c> where a
    /// library holds the content, else <c>quarantine</c> (<see cref="PlanService.MarkForRemovalAsync"/>), and
    /// <see cref="PlanOutcome.Stay"/> sets <c>leave</c>. A rejected action whose kind already has the outcome is only
    /// opened again. Every change goes through the plan's checks and must be released anew; a sensitive action is changed
    /// only on its own.
    /// </summary>
    public async Task SetOutcomeAsync(IReadOnlyList<PlanActionRowViewModel> rows, PlanOutcome outcome)
    {
        if (SelectedPlan is not { } p || rows.Count == 0) return;
        var label = PlanDisplay.OutcomeLabel(outcome);
        var change = rows.Where(r => r.Outcome != outcome).ToList();
        if (change.Count == 0)
        {
            StatusText = rows.Count == 1 ? $"Schon „{label}“." : $"Alle {rows.Count} schon „{label}“.";
            return;
        }
        var single = rows.Count > 1 ? change.Where(r => r.Action.SingleDecision).ToList() : [];
        change = change.Except(single).ToList();
        var reopen = change.Where(r => r.Action.State == PlanActionState.Rejected && PlanDisplay.OutcomeOf(r.Action.Kind) == outcome)
            .Select(r => r.Id).ToList();
        var retype = change.Select(r => r.Id).Except(reopen).ToList();
        try
        {
            var changed = 0;
            var refused = new List<string>();
            PlanState? state = null;
            if (reopen.Count > 0)
            {
                var result = await _plans.ResetDecisionsAsync(p.Plan.Id, new PlanSelection(reopen), decidedBy: null);
                changed += result.Changed.Count;
                state = result.State;
            }
            if (retype.Count > 0)
            {
                var result = outcome switch
                {
                    PlanOutcome.Move => await _plans.SetKindAsync(p.Plan.Id, retype, PlanActionKind.Move, Person),
                    PlanOutcome.Bin => await _plans.MarkForRemovalAsync(p.Plan.Id, retype, Person),
                    _ => await _plans.SetKindAsync(p.Plan.Id, retype, PlanActionKind.Leave, Person)
                };
                changed += result.Changed.Count;
                refused.AddRange(result.Refused.Select(r => r.Message));
                state = result.State;
            }
            refused.AddRange(single.Select(r => $"Action {r.Id}: single_decision (a sensitive action is changed on its own)"));
            if (state is not null) Apply(state);
            StatusText = $"{changed} auf „{label}“ gestellt, neu freizugeben"
                         + (refused.Count == 0 ? "." : $"; abgewiesen: {string.Join("; ", refused)}");
        }
        catch (Exception ex) when (ex is PlanException or JobReadException)
        {
            StatusText = ex.Message;
            Apply(State);
        }
    }

    /// <summary>Sets the exact kind of the selected action through the finer switch in the detail panel.</summary>
    public Task SetSelectedKindAsync(PlanActionKind kind) =>
        RetypeAsync(SelectedAction is { } r ? [r.Id] : [], kind);

    partial void OnSelectedKindChanged(PlanKindOption? value)
    {
        if (_suppress || value is null || SelectedAction is not { } row || row.Action.Kind == value.Kind) return;
        _ = SetSelectedKindAsync(value.Kind);
    }

    /// <summary>A new kind for the actions, with the target the plan service gives it.</summary>
    private async Task RetypeAsync(IReadOnlyList<long> ids, PlanActionKind kind)
    {
        if (SelectedPlan is not { } p || ids.Count == 0) return;
        try
        {
            var result = await _plans.SetKindAsync(p.Plan.Id, ids, kind, Person);
            Apply(result.State);
            StatusText = $"{result.Changed.Count} auf „{PlanDisplay.KindLabel(kind)}“ gestellt, neu freizugeben"
                         + (result.Refused.Count == 0 ? "." : $"; abgewiesen: {string.Join("; ", result.Refused.Select(r => r.Message))}");
        }
        catch (Exception ex) when (ex is PlanException or JobReadException)
        {
            StatusText = ex.Message;
            Apply(State);
        }
    }

    // ================================================================== target

    [RelayCommand] private Task ChangeTargetSelectedAsync() => PickTargetAsync(SelectedRows());
    [RelayCommand] private Task ChangeTargetCheckedAsync() => PickTargetAsync(CheckedRows());

    /// <summary>Opens the folder tree of the plan's libraries for the rows and waits until it is closed.</summary>
    public async Task PickTargetAsync(IReadOnlyList<PlanActionRowViewModel> rows)
    {
        if (State is not { } s || rows.Count == 0 || TargetPicker is not null) return;
        if (s.Libraries.Count == 0)
        {
            StatusText = "Der Plan hat keine Bibliothek, also kein Ziel (sfo compare --against).";
            return;
        }
        var ids = rows.Select(r => r.Id).ToList();
        var picker = new PlanTargetPickerViewModel(rows, s.Libraries, _libraries, folder => SetTargetAsync(ids, folder));
        TargetPicker = picker;
        try
        {
            await picker.LoadAsync();
            await picker.Closed;
        }
        finally
        {
            TargetPicker = null;
        }
    }

    /// <summary>
    /// Sets the target of the actions to <paramref name="folder"/> plus their own name, through
    /// <see cref="PlanService.ChangeAsync"/> and its checks; an action that does not move yet becomes <c>move</c>.
    /// The plan stores all or none, so a refused action is dropped and the rest is tried again. In a selection a
    /// sensitive action is passed over. Returns why nothing was set, or null when at least one target was set.
    /// </summary>
    public async Task<string?> SetTargetAsync(IReadOnlyList<long> ids, string folder)
    {
        if (SelectedPlan is not { } p || State is not { } s) return "Kein Plan offen.";
        var byId = s.Actions.ToDictionary(a => a.Action.Id, a => a.Action);
        var refused = new List<string>();
        var changes = new List<PlanChange>();
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var a)) continue;
            if (ids.Count > 1 && a.SingleDecision)
            {
                refused.Add($"{Path.GetFileName(a.SourcePath)}: sensibel, nur einzeln");
                continue;
            }
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(a.SourcePath));
            var kind = PlanDisplay.OutcomeOf(a.Kind) == PlanOutcome.Move ? (PlanActionKind?)null : PlanActionKind.Move;
            changes.Add(new PlanChange(id, kind, PlanChecks.Join(folder, name)));
        }

        var changed = 0;
        while (changes.Count > 0)
        {
            try
            {
                Apply(await _plans.ChangeAsync(p.Plan.Id, changes, Person));
                changed = changes.Count;
                break;
            }
            catch (PlanException ex) when (ex.Failure == PlanFailure.ChangeRefused && ex.Refused.Count > 0)
            {
                var bad = ex.Refused.Select(r => r.ActionId).ToHashSet();
                refused.AddRange(ex.Refused.Select(r =>
                    (byId.TryGetValue(r.ActionId, out var a) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(a.SourcePath)) : $"Aktion {r.ActionId}")
                    + ": " + (r.Problems.Count > 0 ? string.Join(", ", r.Problems.Select(PlanDisplay.ProblemLabel)) : r.Message)));
                changes = changes.Where(c => !bad.Contains(c.ActionId)).ToList();
            }
            catch (Exception ex) when (ex is PlanException or JobReadException)
            {
                refused.Add(ex.Message);
                break;
            }
        }

        var where = PlanActionRowViewModel.ShortTarget(folder, s.Libraries.Select(l => l.RootPath).ToList());
        if (changed == 0)
        {
            var why = "Abgewiesen: " + string.Join("; ", refused);
            StatusText = why;
            return why;
        }
        StatusText = $"Ziel {where} für {changed} gesetzt, neu freizugeben"
                     + (refused.Count == 0 ? "." : $"; abgewiesen: {string.Join("; ", refused)}");
        return null;
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
            DetailProblems = DetailDecision = DetailDescription = DetailMarker = DetailFacts = DetailMedia = string.Empty;
            DetailConsequence = DetailRelease = string.Empty;
            OutcomeChoices = [];
            return;
        }

        var a = row.Action;
        DetailTitle = row.Name;
        DetailSource = a.SourcePath;
        DetailTarget = a.TargetPath ?? "kein Ziel";
        DetailRecommended = $"Vorschlag: {PlanDisplay.KindLabel(a.Recommended)}" + (a.Kind != a.Recommended ? " (geändert)" : string.Empty);
        DetailReason = a.Reason;
        DetailNote = a.Note ?? string.Empty;
        DetailProblems = row.HasProblems ? "Probleme: " + row.ProblemsText : string.Empty;
        DetailDecision = $"Herkunft {PlanDisplay.OriginLabel(a.Origin)}"
                         + (a.DecidedBy is { } by ? $" · entschieden von {PlanDisplay.OriginLabel(by)}" : string.Empty)
                         + (a.DecidedAt is { } at ? $" am {at.ToLocalTime():dd.MM.yyyy HH:mm}" : string.Empty);
        DetailConsequence = row.Consequence;
        DetailOutcomeColor = row.OutcomeColor;
        DetailRelease = PlanDisplay.StateLabel(a.State) + (a.Sensitive && a.State == PlanActionState.Proposed ? " · sensibel: nur hier einzeln freizugeben" : string.Empty);
        DetailReleaseColor = row.StateColor;
        OutcomeChoices = PlanDisplay.Outcomes.Select(o => new PlanOutcomeChoice(o, o == row.Outcome)).ToList();
        DetailDescription = DetailMarker = DetailFacts = DetailMedia = string.Empty;

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
        DetailMedia = MediaLine(file, facts, shows);

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

    /// <summary>
    /// The key facts of a video or audio file in one line, large enough to tell clutter at a glance (duration, size of
    /// the picture, device, date; artist, title, album). From the stored facts; empty for other files.
    /// </summary>
    public static string MediaLine(FileNode file, IReadOnlyList<FileFact> facts, bool showsFreeText)
    {
        if (file.FileType is not (FileType.Video or FileType.Audio)) return string.Empty;
        string? Fact(FileFactKey key) => facts.FirstOrDefault(f => f.Key == key && (showsFreeText || !f.Key.IsFreeText()))?.Value;
        var parts = new List<string> { file.FileType == FileType.Video ? "Video" : "Audio" };
        if (Fact(FileFactKey.DurationSeconds) is { } d
            && double.TryParse(d, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            parts.Add(TimeSpan.FromSeconds(Math.Round(seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss"));
        if (Fact(FileFactKey.Width) is { } w && Fact(FileFactKey.Height) is { } h)
            parts.Add($"{w} × {h}");
        foreach (var key in new[] { FileFactKey.Artist, FileFactKey.Title, FileFactKey.Album, FileFactKey.Device })
            if (Fact(key) is { Length: > 0 } v) parts.Add(v);
        if (Fact(FileFactKey.TakenAt) is { Length: >= 10 } taken) parts.Add(taken[..10]);
        parts.Add(PlanDisplay.Size(file.Size));
        return string.Join(" · ", parts);
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
            StatusText = "Schritt 1: nichts zu tun (keine freigegebene Aktion ohne Problem).";
            return;
        }
        var lines = new List<string>(PlanOutcomeSummary.BeforeApply(
            State?.Actions.Select(s => s.Action).ToList() ?? [],
            run.Select(i => i.ActionId).ToHashSet(),
            State?.Libraries.Select(l => l.RootPath).ToList() ?? [],
            State?.Job.RootPath ?? string.Empty,
            dry.KeepSource))
        {
            $"Jetzt, Schritt 1: {PlanDisplay.Files(run.Sum(i => i.Files))} ({PlanDisplay.Size(run.Sum(i => i.Bytes))}) werden in die Bibliothek kopiert "
            + "und per voller Prüfsumme geprüft. Kein Original wird in diesem Schritt angefasst."
        };
        if (!await ConfirmAsync("Ausführen, Schritt 1 von 2", lines, "Kopieren und prüfen", isDanger: false)) return;
        await RunAsync("Schritt 1 · kopieren und prüfen", run.Count,
            (o, ct) => _executor.ApplyAsync(p.Plan.Id, o, ct));
    }

    /// <summary>Step 2: the checked originals go into the hold folder, after a confirmation with what goes and the room freed.</summary>
    [RelayCommand]
    public async Task RemoveSourcesAsync()
    {
        if (SelectedPlan is not { } p || IsRunning) return;
        var dry = await _executor.RemoveSourcesAsync(p.Plan.Id, new PlanRunOptions([], Execute: false));
        ShowReport("Trockenlauf · Schritt 2 (Originale in den Papierkorb)", dry);
        var run = dry.Items.Where(i => i.Outcome == "would_run").ToList();
        if (run.Count == 0)
        {
            StatusText = "Schritt 2: nichts zu tun (kein Original wartet; erst Schritt 1 ausführen).";
            return;
        }
        var lines = new List<string>(PlanOutcomeSummary.BeforeRemove(
            State?.Actions.Select(s => s.Action).ToList() ?? [],
            run.Select(i => i.ActionId).ToHashSet(),
            State?.Job.RootPath ?? string.Empty))
        {
            $"Frei wird: {PlanDisplay.Size(run.Sum(i => i.Bytes))} ({PlanDisplay.Files(run.Sum(i => i.Files))})."
        };
        if (!await ConfirmAsync("Ausführen, Schritt 2 von 2: Originale in den Papierkorb?", lines, "In den Papierkorb", isDanger: true)) return;
        await RunAsync("Schritt 2 · Originale in den Papierkorb", run.Count,
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
