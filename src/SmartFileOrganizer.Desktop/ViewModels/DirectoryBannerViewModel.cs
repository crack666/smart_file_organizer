using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>A verdict the person can set.</summary>
public sealed record VerdictOption(DirectoryVerdictKind Kind)
{
    public string Label => BrowserDisplay.VerdictLabel(Kind.ToCode());
}

/// <summary>A retention value the person can set; null leaves the one of rule or model.</summary>
public sealed record RetentionOption(RetentionValue? Value)
{
    public string Label => Value is { } v ? BrowserDisplay.RetentionLabel(v.ToCode()) : "unverändert";
}

/// <summary>
/// What the job knows about the selected folder, above the file table: verdict with its reason, purpose, retention,
/// why it was raised to mixed, and the person's own verdict, set and taken back here
/// (https://github.com/crack666/smart_file_organizer/issues/14).
/// </summary>
public partial class DirectoryBannerViewModel : ViewModelBase
{
    private readonly DirectoryQueryService _dirQuery;
    private readonly DirectoryReviewService _review;

    private long _jobId;
    private long _nodeId;
    private int _loadVersion;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _sizeText = string.Empty;

    [ObservableProperty] private string _verdictLabel = string.Empty;
    [ObservableProperty] private string _verdictColor = BrowserDisplay.VerdictColor(null);
    [ObservableProperty] private bool _hasVerdict;

    /// <summary>Who decided, <c>Modell</c>, <c>Regel inside-unit</c> or <c>von Hand</c>; or that nobody has yet.</summary>
    [ObservableProperty] private string _sourceText = string.Empty;

    [ObservableProperty] private string _retentionLabel = string.Empty;
    [ObservableProperty] private string _retentionColor = BrowserDisplay.RetentionColor(null);
    [ObservableProperty] private bool _hasRetention;

    [ObservableProperty] private string _purpose = string.Empty;
    [ObservableProperty] private string _reason = string.Empty;

    /// <summary>Why a uniform folder was raised to mixed; empty when it was not.</summary>
    [ObservableProperty] private string _escalationText = string.Empty;

    /// <summary>When the person set the verdict and what rule or model said before; empty without a correction.</summary>
    [ObservableProperty] private string _overrideText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetOverrideCommand))]
    private bool _isOverridden;

    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private string _skipText = string.Empty;
    [ObservableProperty] private string _sensitiveText = string.Empty;

    /// <summary>The model's summary of the analysed folder.</summary>
    [ObservableProperty] private string _summary = string.Empty;

    // ── Own verdict ───────────────────────────────────────────────────────
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private ObservableCollection<VerdictOption> _verdictOptions = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectText))]
    [NotifyCanExecuteChangedFor(nameof(SaveOverrideCommand))]
    private VerdictOption? _selectedVerdict;

    [ObservableProperty] private RetentionOption _selectedRetention = RetentionOptions[0];
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _editStatus = string.Empty;

    public static IReadOnlyList<RetentionOption> RetentionOptions { get; } =
        [new(null), .. Enum.GetValues<RetentionValue>().Select(v => new RetentionOption(v))];

    /// <summary>What the chosen verdict means for the files.</summary>
    public string EffectText => SelectedVerdict is { } v ? BrowserDisplay.VerdictEffect(v.Kind) : string.Empty;

    /// <summary>A verdict was set or taken back: path of the folder and its verdict now.</summary>
    public event Action<string, DirectoryVerdict?>? VerdictChanged;

    public DirectoryBannerViewModel(DirectoryQueryService dirQuery, DirectoryReviewService review)
    {
        _dirQuery = dirQuery;
        _review = review;
    }

    /// <summary>
    /// Shows a folder. Called again for the same folder (while the analysis runs) it refreshes what is shown and leaves a
    /// begun correction alone. <paramref name="facts"/> may come later (<see cref="ApplyFacts"/>).
    /// </summary>
    public async Task LoadAsync(long jobId, string path, DirectoryItem? facts, CancellationToken ct = default)
    {
        var version = ++_loadVersion;
        var node = await _dirQuery.GetDirectoryAsync(jobId, path, ct);
        var verdict = await _dirQuery.GetVerdictAsync(jobId, path, ct);
        if (version != _loadVersion) return; // another folder was selected meanwhile
        if (node is null)
        {
            Clear();
            return;
        }

        var sameFolder = _jobId == jobId && Path == path && IsVisible;
        (_jobId, _nodeId) = (jobId, node.Id);
        Path = path;
        Title = node.Name;
        SkipText = BrowserDisplay.SkipText(node.AnalysisSkip);
        if (!sameFolder)
        {
            IsEditing = false;
            EditStatus = string.Empty;
        }
        ApplyFacts(facts);
        Show(verdict);
        if (!IsEditing) FillEditor(verdict);
        IsVisible = true;
    }

    /// <summary>Size, marked files and summary, read with the tree of the whole job.</summary>
    public void ApplyFacts(DirectoryItem? facts)
    {
        SizeText = facts is null ? string.Empty : $"{PlanDisplay.Files(facts.Files)} · {PlanDisplay.Size(facts.SizeBytes)}";
        Summary = facts?.Summary ?? string.Empty;
        SensitiveText = facts switch
        {
            null => string.Empty,
            { SensitiveFiles: > 0 } => $"{PlanDisplay.Files(facts.SensitiveFiles)} sensibel",
            { Sensitive.Count: > 0 } => $"Ordner sensibel: {CodeLabels.Markers(facts.Sensitive, DisplayLanguage.German)}",
            _ => string.Empty
        };
    }

    public void Clear()
    {
        _loadVersion++;
        _jobId = _nodeId = 0;
        IsVisible = IsEditing = false;
        Path = Title = SkipText = EditStatus = string.Empty;
        ApplyFacts(null);
        Show(null);
        FillEditor(null);
    }

    [RelayCommand]
    private void ToggleEdit()
    {
        IsEditing = !IsEditing;
        EditStatus = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveOverrideAsync()
    {
        if (SelectedVerdict is not { } chosen) return;
        await ChangeAsync(id => _review.ApplyOverrideAsync(id, chosen.Kind, SelectedRetention.Value, Note),
            $"Gespeichert: {chosen.Label}. Gilt für diesen Ordner; beurteilte Unterordner behalten ihr Urteil.");
    }

    [RelayCommand(CanExecute = nameof(IsOverridden))]
    private Task ResetOverrideAsync() =>
        ChangeAsync(id => _review.ResetAsync(id), "Eigenes Urteil zurückgenommen.");

    private bool CanSave() => SelectedVerdict is not null;

    private async Task ChangeAsync(Func<long, Task> change, string done)
    {
        if (_nodeId <= 0) return;
        var (jobId, path, nodeId) = (_jobId, Path, _nodeId);
        try
        {
            await change(nodeId);
            var verdict = await _dirQuery.GetVerdictAsync(jobId, path);
            if (nodeId == _nodeId)
            {
                Show(verdict);
                FillEditor(verdict);
                IsEditing = false;
                EditStatus = done;
            }
            VerdictChanged?.Invoke(path, verdict);
        }
        catch (Exception ex)
        {
            EditStatus = $"Nicht gespeichert: {ex.Message}";
        }
    }

    private void Show(DirectoryVerdict? verdict)
    {
        var code = verdict?.Verdict?.ToCode();
        HasVerdict = code is not null;
        VerdictLabel = BrowserDisplay.VerdictLabel(code);
        VerdictColor = BrowserDisplay.VerdictColor(code);
        SourceText = verdict switch
        {
            null => "noch kein Urteil",
            { Verdict: null } => "kein gültiges Urteil",
            { Source: VerdictSource.Rule, Rule: { Length: > 0 } rule } => $"Regel {rule}",
            _ => BrowserDisplay.SourceLabel(verdict.Source.ToCode())
        };
        var retention = verdict?.Retention?.ToCode();
        HasRetention = retention is not null;
        RetentionLabel = BrowserDisplay.RetentionLabel(retention);
        RetentionColor = BrowserDisplay.RetentionColor(retention);
        Purpose = verdict?.Purpose ?? string.Empty;
        Reason = verdict?.Reason ?? string.Empty;
        ErrorText = verdict?.Error is { Length: > 0 } error ? $"Das Modell gab kein gültiges Urteil: {error}" : string.Empty;

        // The escalation belongs to the verdict of rule or model, also below a correction.
        var judged = verdict?.Replaced ?? verdict;
        EscalationText = judged?.EscalatedFrom is { } from
            ? $"Hochgestuft von {BrowserDisplay.VerdictLabel(from.ToCode())}" + (judged.EscalationReason is { Length: > 0 } why ? $": {why}" : string.Empty)
            : string.Empty;

        IsOverridden = verdict?.Override is not null;
        OverrideText = verdict?.Override is not { } own ? string.Empty
            : $"Von Hand gesetzt am {own.OverriddenAt.ToLocalTime():dd.MM.yyyy}; vorher: " + (verdict.Replaced switch
            {
                null => "kein Urteil",
                { Verdict: { } kind } before => $"{BrowserDisplay.VerdictLabel(kind.ToCode())} ({BrowserDisplay.SourceLabel(before.Source.ToCode())})",
                _ => "kein gültiges Urteil"
            });
    }

    /// <summary>The four verdicts a person can set, and the present one when it is another (<c>leer</c>, <c>Abzug</c>).</summary>
    private void FillEditor(DirectoryVerdict? verdict)
    {
        List<DirectoryVerdictKind> kinds =
            [DirectoryVerdictKind.Unit, DirectoryVerdictKind.Uniform, DirectoryVerdictKind.Mixed, DirectoryVerdictKind.System];
        if (verdict?.Verdict is { } now && !kinds.Contains(now)) kinds.Add(now);
        VerdictOptions = new ObservableCollection<VerdictOption>(kinds.Select(k => new VerdictOption(k)));
        SelectedVerdict = VerdictOptions.FirstOrDefault(o => o.Kind == verdict?.Verdict);
        SelectedRetention = RetentionOptions.First(o => o.Value == verdict?.Override?.Retention);
        Note = verdict?.Override?.Note ?? string.Empty;
    }
}
