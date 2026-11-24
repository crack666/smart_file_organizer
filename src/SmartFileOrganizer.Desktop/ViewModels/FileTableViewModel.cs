using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>A choice in a filter of the file table; a null value means all.</summary>
public sealed record FileFilterOption<T>(string Label, T? Value) where T : struct
{
    public override string ToString() => Label;
}

/// <summary>
/// The files of one folder: filtered by category, state, low confidence, marked and temporary, and corrected together
/// (https://github.com/crack666/smart_file_organizer/issues/14). The filters stay when another folder is selected; a
/// check mark counts only while its row is shown, so nothing hidden is ever corrected.
/// </summary>
public partial class FileTableViewModel : ViewModelBase
{
    private readonly FileQueryService _fileQuery;
    private readonly ReviewService _reviewService;
    private readonly double _lowConfidence;

    private List<FileRowViewModel> _all = [];
    private (long JobId, string Path) _shown;
    private int _loadVersion;
    private bool _suspendFilter;

    public Func<FileRowViewModel, Task>? ReanalyzeFileAction { get; set; }

    /// <summary>Ids of files that just got a common category.</summary>
    public event Action<IReadOnlyCollection<long>>? BulkApplied;

    /// <summary>The rows the filters let through.</summary>
    [ObservableProperty] private ObservableCollection<FileRowViewModel> _rows = [];
    [ObservableProperty] private FileRowViewModel? _selectedRow;
    [ObservableProperty] private bool _isLoading;

    // ── Filters ───────────────────────────────────────────────────────────
    [ObservableProperty] private FileFilterOption<FileCategory> _selectedCategoryFilter = CategoryFilters[0];
    [ObservableProperty] private FileFilterOption<FileNodeStatus> _selectedStatusFilter = StatusFilters[0];
    [ObservableProperty] private bool _onlyLowConfidence;
    [ObservableProperty] private bool _onlySensitive;
    [ObservableProperty] private bool _onlyTemporary;

    /// <summary><c>12 Dateien</c>, or with a filter <c>3 von 12 Dateien</c>.</summary>
    [ObservableProperty] private string _countText = string.Empty;

    /// <summary>Why the table is empty; empty when it shows rows.</summary>
    [ObservableProperty] private string _emptyText = string.Empty;

    // ── Common correction ─────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCategoryToCheckedCommand))]
    private FileCategory? _bulkCategory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckedText), nameof(HasChecked))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCategoryToCheckedCommand))]
    private int _checkedCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCategoryToCheckedCommand))]
    private bool _isBulkRunning;

    [ObservableProperty] private string _bulkStatus = string.Empty;

    public bool HasChecked => CheckedCount > 0;
    public string CheckedText => CheckedCount == 1 ? "1 Datei ausgewählt" : $"{CheckedCount} Dateien ausgewählt";

    public static IReadOnlyList<FileFilterOption<FileCategory>> CategoryFilters { get; } =
        [new("Alle Kategorien", null), .. Enum.GetValues<FileCategory>().Select(c => new FileFilterOption<FileCategory>(c.ToString(), c))];

    public static IReadOnlyList<FileFilterOption<FileNodeStatus>> StatusFilters { get; } =
        [new("Alle Zustände", null), .. Enum.GetValues<FileNodeStatus>().Select(s => new FileFilterOption<FileNodeStatus>(BrowserDisplay.StatusLabel(s), s))];

    public static IReadOnlyList<FileCategory> AllCategories => FileRowViewModel.AllCategories;

    public string LowConfidenceHint =>
        $"Konfidenz unter {_lowConfidence:P0}, noch nicht bestätigt oder korrigiert";

    public bool IsFiltered => SelectedCategoryFilter.Value is not null || SelectedStatusFilter.Value is not null
        || OnlyLowConfidence || OnlySensitive || OnlyTemporary;

    public FileTableViewModel(FileQueryService fileQuery, ReviewService reviewService, SecondCheckOptions secondCheck)
    {
        _fileQuery = fileQuery;
        _reviewService = reviewService;
        // The bound below which the analysis itself asks a second time.
        _lowConfidence = secondCheck.BelowConfidence;
    }

    /// <summary>Shows a folder. The same folder again (progress of the analysis) keeps selection and check marks.</summary>
    public async Task LoadAsync(long jobId, string directoryPath, CancellationToken ct = default)
    {
        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            var files = await _fileQuery.GetFilesInDirectoryAsync(jobId, directoryPath, ct);
            if (version != _loadVersion) return; // another folder was selected meanwhile

            var same = _shown == (jobId, directoryPath);
            var selected = same ? SelectedRow?.Id : null;
            var checkedIds = same ? _all.Where(r => r.IsChecked).Select(r => r.Id).ToHashSet() : new HashSet<long>();
            _shown = (jobId, directoryPath);
            if (!same) BulkStatus = string.Empty;

            SetRows(files.Select(f => FileRowViewModel.From(f, _reviewService, ReanalyzeFileAction)).ToList(), checkedIds);
            ApplyFilter(selected);
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    /// <summary>Shows the hits of a search over the job instead of a folder, each with its folder.</summary>
    public void ShowSearch(JobSearchResult search)
    {
        _loadVersion++;
        IsLoading = false;
        _shown = (search.JobId, SearchPath);
        BulkStatus = string.Empty;
        _moreHits = search.FileIds.Count - search.Files.Count;
        SetRows(search.Files.Select(f => FileRowViewModel.From(f, _reviewService, ReanalyzeFileAction, withFolder: true)).ToList(), new HashSet<long>());
        ApplyFilter(null);
    }

    /// <summary>True while the table shows the hits of a search.</summary>
    public bool IsSearch => _shown.Path == SearchPath;

    private const string SearchPath = "\0search";
    private int _moreHits;

    public void Clear()
    {
        _loadVersion++;
        IsLoading = false;
        _shown = default;
        BulkStatus = string.Empty;
        SetRows([], new HashSet<long>());
        ApplyFilter(null);
    }

    /// <summary>All filters off.</summary>
    [RelayCommand]
    private void ResetFilters()
    {
        _suspendFilter = true;
        SelectedCategoryFilter = CategoryFilters[0];
        SelectedStatusFilter = StatusFilters[0];
        OnlyLowConfidence = OnlySensitive = OnlyTemporary = false;
        _suspendFilter = false;
        ApplyFilter(SelectedRow?.Id);
    }

    [RelayCommand]
    private void CheckAllShown()
    {
        foreach (var row in Rows) row.IsChecked = true;
    }

    [RelayCommand]
    private void ClearChecks()
    {
        foreach (var row in _all) row.IsChecked = false;
    }

    /// <summary>Gives every checked file the chosen category, each as a correction of its own.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyCategory))]
    private async Task ApplyCategoryToCheckedAsync()
    {
        if (BulkCategory is not { } category) return;
        var ids = Rows.Where(r => r.IsChecked).Select(r => r.Id).ToList();
        if (ids.Count == 0) return;

        IsBulkRunning = true;
        BulkStatus = $"Korrigiere {PlanDisplay.Files(ids.Count)}…";
        var applied = new List<long>();
        try
        {
            // Off the UI thread: several writes per file.
            var result = await Task.Run(() => _reviewService.ApplyCategoryAsync(ids, category, applied.Add));
            BulkStatus = $"{PlanDisplay.Files(result.Applied)}: Kategorie {category} gesetzt."
                + (result.Errors.Count > 0 ? $" {result.Errors.Count} nicht gespeichert: {result.Errors[0]}" : string.Empty);
        }
        catch (Exception ex)
        {
            BulkStatus = $"{PlanDisplay.Files(applied.Count)} gespeichert, dann abgebrochen: {ex.Message}";
        }
        finally
        {
            IsBulkRunning = false;
        }

        // The rows are found by id: the table may have been loaded again meanwhile. What failed stays checked.
        var done = applied.ToHashSet();
        foreach (var row in _all.Where(r => done.Contains(r.Id)))
        {
            row.ShowCorrection(category);
            row.IsChecked = false;
        }
        ApplyFilter(SelectedRow?.Id);
        BulkApplied?.Invoke(done);
    }

    private bool CanApplyCategory() => !IsBulkRunning && BulkCategory is not null && CheckedCount > 0;

    partial void OnSelectedCategoryFilterChanged(FileFilterOption<FileCategory> value) => FilterChanged();
    partial void OnSelectedStatusFilterChanged(FileFilterOption<FileNodeStatus> value) => FilterChanged();
    partial void OnOnlyLowConfidenceChanged(bool value) => FilterChanged();
    partial void OnOnlySensitiveChanged(bool value) => FilterChanged();
    partial void OnOnlyTemporaryChanged(bool value) => FilterChanged();

    private void FilterChanged()
    {
        if (!_suspendFilter) ApplyFilter(SelectedRow?.Id);
    }

    private bool Passes(FileRowViewModel row) =>
        (SelectedCategoryFilter.Value is not { } category || row.SelectedCategory == category)
        && (SelectedStatusFilter.Value is not { } status || row.Status == status)
        && (!OnlyLowConfidence || row.HasLowConfidence(_lowConfidence))
        && (!OnlySensitive || row.IsSensitive)
        && (!OnlyTemporary || row.IsTemporary);

    private void SetRows(List<FileRowViewModel> rows, IReadOnlySet<long> checkedIds)
    {
        foreach (var row in _all) row.PropertyChanged -= OnRowChanged;
        _all = rows;
        foreach (var row in _all)
        {
            row.IsChecked = checkedIds.Contains(row.Id);
            row.PropertyChanged += OnRowChanged;
        }
    }

    private void ApplyFilter(long? selectedId)
    {
        Rows.Clear();
        foreach (var row in _all)
        {
            if (Passes(row)) Rows.Add(row);
            else row.IsChecked = false;
        }
        SelectedRow = selectedId is { } id ? Rows.FirstOrDefault(r => r.Id == id) : null;
        CountChecked();
        CountText = IsFiltered ? $"{Rows.Count} von {PlanDisplay.Files(_all.Count)}" : PlanDisplay.Files(_all.Count);
        if (IsSearch) CountText += _moreHits > 0 ? $" in der Suche (die ersten von {_all.Count + _moreHits})" : " in der Suche";
        EmptyText = Rows.Count > 0 ? string.Empty
            : _all.Count > 0 ? IsSearch ? "Kein Filtertreffer in der Suche." : "Kein Filtertreffer in diesem Ordner."
            : IsSearch ? "Keine Datei passt zur Suche."
            : _shown.JobId > 0 ? "Keine Dateien direkt in diesem Ordner." : "Links einen Ordner wählen.";
        OnPropertyChanged(nameof(IsFiltered));
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileRowViewModel.IsChecked)) CountChecked();
    }

    private void CountChecked() => CheckedCount = Rows.Count(r => r.IsChecked);
}
