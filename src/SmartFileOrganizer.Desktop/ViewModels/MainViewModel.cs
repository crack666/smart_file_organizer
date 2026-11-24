using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Desktop.Services;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Hosting;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ScanJobService _scanJobService;
    private readonly FileQueryService _fileQueryService;
    private readonly AiClassificationCoordinator _aiCoordinator;
    private readonly IOllamaService _ollamaService;
    private readonly OllamaOptions _ollamaOptions;
    private readonly OllamaSettingsService _ollamaSettingsService;
    private readonly FolderTreeViewModel _folderTree;
    private readonly FileTableViewModel _fileTable;
    private readonly FileDetailViewModel _fileDetail;
    private readonly ScanProgressViewModel _scanProgress;
    private readonly AiProgressViewModel _aiProgress;
    private readonly FocusedRescanService _focusedRescanService;
    private readonly DirectoryBannerViewModel _directory;
    private readonly ScanExportService _export;
    private readonly SensitiveOutputOptions _exportOptions;
    private readonly JobSearchService _searchService;

    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(350);

    private long _activeJobId;
    private ScanJob? _activeJob;
    private CancellationTokenSource? _searchCts;
    private bool _suspendSearch;
    private FolderTreeItemViewModel? _folderBeforeSearch;
    private CancellationTokenSource? _uiCts;
    private string _selectedDirectoryPath = string.Empty;
    private long? _selectedFileId;
    private DateTime _lastAiRefreshUtc = DateTime.MinValue;

    // Sub-viewmodels exposed to the view
    public FolderTreeViewModel FolderTree => _folderTree;
    public FileTableViewModel FileTable => _fileTable;
    public FileDetailViewModel FileDetail => _fileDetail;
    public ScanProgressViewModel ScanProgress => _scanProgress;
    public AiProgressViewModel AiProgress => _aiProgress;
    public PlanViewModel Plan { get; }

    /// <summary>Findings and bundles of the open job: what it opens with.</summary>
    public FindingsViewModel Findings { get; }

    /// <summary>The jobs of the database in the side bar.</summary>
    public JobBarViewModel JobBar { get; }

    /// <summary>The selected folder, above the file table.</summary>
    public DirectoryBannerViewModel Directory => _directory;

    /// <summary>Which database the window works on; always visible in the status bar.</summary>
    public string DatabaseText => Plan.DatabaseText;

    /// <summary>The plan view instead of the welcome screen or the browser.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcomeVisible))]
    [NotifyPropertyChangedFor(nameof(IsBrowserVisible))]
    [NotifyPropertyChangedFor(nameof(IsFindingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsSidebarVisible))]
    [NotifyPropertyChangedFor(nameof(IsJobViewVisible))]
    private bool _isShowingPlan;

    /// <summary>The findings instead of folder tree and file table (https://github.com/crack666/smart_file_organizer/issues/27).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowserVisible))]
    [NotifyPropertyChangedFor(nameof(IsFindingsVisible))]
    [NotifyPropertyChangedFor(nameof(IsSidebarVisible))]
    private bool _isShowingFindings;

    public bool IsWelcomeVisible => IsShowingWelcome && !IsShowingPlan;
    public bool IsBrowserVisible => !IsShowingWelcome && !IsShowingPlan && !IsShowingFindings;
    public bool IsFindingsVisible => !IsShowingWelcome && !IsShowingPlan && IsShowingFindings;

    /// <summary>A job is open: the line with its views and the search is there.</summary>
    public bool IsJobOpen => _activeJobId > 0 && !IsShowingWelcome;

    /// <summary>Findings or folders of the open job; the search and the CSV export belong to them.</summary>
    public bool IsJobViewVisible => IsJobOpen && !IsShowingPlan;

    private bool _sidebarInFindings = true;
    private bool _sidebarInFolders;

    /// <summary>Jobs and quick filters at the side: open beside the findings, closed beside the folders, until it is switched.</summary>
    public bool IsSidebarOpen
    {
        get => IsShowingFindings ? _sidebarInFindings : _sidebarInFolders;
        set
        {
            if (IsShowingFindings) _sidebarInFindings = value;
            else _sidebarInFolders = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSidebarVisible));
        }
    }

    public bool IsSidebarVisible => IsJobViewVisible && IsSidebarOpen;

    partial void OnIsShowingFindingsChanged(bool value) => OnPropertyChanged(nameof(IsSidebarOpen));

    partial void OnIsShowingWelcomeChanged(bool value) => JobViewChanged();

    private void JobViewChanged()
    {
        OnPropertyChanged(nameof(IsWelcomeVisible));
        OnPropertyChanged(nameof(IsBrowserVisible));
        OnPropertyChanged(nameof(IsFindingsVisible));
        OnPropertyChanged(nameof(IsJobOpen));
        OnPropertyChanged(nameof(IsJobViewVisible));
        OnPropertyChanged(nameof(IsSidebarVisible));
    }

    /// <summary>Searched for in name, path and summary of the open job, and in its findings and bundles.</summary>
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>"12 Dateien"; empty without a search.</summary>
    [ObservableProperty] private string _searchStatus = string.Empty;

    [ObservableProperty] private string _rootPath = string.Empty;
    [ObservableProperty] private bool _isScanRunning;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeScanCommand))]
    private bool _canStartScan;
    [ObservableProperty] private bool _isShowingWelcome;
    [ObservableProperty] private ObservableCollection<RecentScanItemViewModel> _recentScans = [];
    [ObservableProperty] private ObservableCollection<ScanJob> _jobs = [];
    [ObservableProperty] private ScanJob? _selectedJob;
    [ObservableProperty] private ObservableCollection<OllamaModelViewModel> _availableOllamaModels = [];
    [ObservableProperty] private OllamaModelViewModel? _selectedOllamaModel;

    /// <summary>Result of the last CSV export; empty before the first.</summary>
    [ObservableProperty] private string _exportStatusText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    private bool _isExporting;

    partial void OnSelectedOllamaModelChanged(OllamaModelViewModel? value)
    {
        if (value != null)
        {
            _ollamaOptions.Model = value.Name;
            OllamaStatusText = $"Active model: {value.Name}";
        }
    }
    [ObservableProperty] private string _ollamaBaseUrl = string.Empty;
    [ObservableProperty] private string _ollamaKeepAlive = string.Empty;
    [ObservableProperty] private string _ollamaParallelRequests = "1";
    [ObservableProperty] private string _ollamaStatusText = "Ollama settings loaded.";
    [ObservableProperty] private bool _isLoadingOllamaModels;
    [ObservableProperty] private bool _isSavingOllamaSettings;

    // Injected by the view for folder-picker dialog
    public Func<Task<string?>>? PickFolderDialog { get; set; }

    // Injected by the view for the save dialog of the CSV export; receives the suggested file name
    public Func<string, Task<string?>>? PickSaveFileDialog { get; set; }

    // Injected by the view to open the AI prompt settings dialog;
    // receives the pre-populated VM and returns true when the user confirms
    public Func<AiPromptSettingsViewModel, Task<bool>>? OpenPromptSettingsDialog { get; set; }

    public MainViewModel(
        ScanJobService scanJobService,
        FileQueryService fileQueryService,
        AiClassificationCoordinator aiCoordinator,
        IOllamaService ollamaService,
        OllamaOptions ollamaOptions,
        OllamaSettingsService ollamaSettingsService,
        FolderTreeViewModel folderTree,
        FileTableViewModel fileTable,
        FileDetailViewModel fileDetail,
        ScanProgressViewModel scanProgress,
        AiProgressViewModel aiProgress,
        FocusedRescanService focusedRescanService,
        PlanViewModel plan,
        DirectoryBannerViewModel directory,
        ScanExportService export,
        SensitiveOutputOptions exportOptions,
        FindingsViewModel findings,
        JobBarViewModel jobBar,
        JobSearchService search)
    {
        Findings = findings;
        JobBar = jobBar;
        _searchService = search;
        Plan = plan;
        _directory = directory;
        _export = export;
        _exportOptions = exportOptions;
        _scanJobService = scanJobService;
        _fileQueryService = fileQueryService;
        _aiCoordinator = aiCoordinator;
        _ollamaService = ollamaService;
        _ollamaOptions = ollamaOptions;
        _ollamaSettingsService = ollamaSettingsService;
        _folderTree = folderTree;
        _fileTable = fileTable;
        _fileDetail = fileDetail;
        _scanProgress = scanProgress;
        _aiProgress = aiProgress;
        _focusedRescanService = focusedRescanService;

        _scanJobService.ProgressChanged += OnProgressChanged;
        _scanJobService.JobStateChanged += OnJobStateChanged;
        _aiCoordinator.ProgressChanged += OnAiProgressChanged;
        _aiCoordinator.StateChanged += OnAiStateChanged;

        _folderTree.DirectorySelected += OnDirectorySelected;
        _folderTree.FactsLoaded += (_, _) =>
        {
            if (_directory.IsVisible) _directory.ApplyFacts(_folderTree.FactsOf(_directory.Path));
        };
        _folderTree.RescanDirectoryAction = RescanDirectoryAsync;
        _directory.VerdictChanged += OnVerdictChanged;
        _fileTable.BulkApplied += ids =>
        {
            if (_selectedFileId is { } id && ids.Contains(id)) _ = LoadFileDetailAsync(id);
        };
        _fileDetail.ReviewApplied += OnReviewApplied;
        _fileTable.ReanalyzeFileAction = ReanalyzeFileAsync;

        _fileTable.PropertyChanged += (_, e) =>
        {
            // Filled again (progress of the analysis) is no new selection: the row comes back by its id.
            if (_fileTable.IsLoading) return;

            if (e.PropertyName == nameof(FileTableViewModel.SelectedRow) &&
                _fileTable.SelectedRow != null)
            {
                if (_selectedFileId == _fileTable.SelectedRow.Id) return;
                _selectedFileId = _fileTable.SelectedRow.Id;
                _ = LoadFileDetailAsync(_fileTable.SelectedRow.Id);
            }
            else if (e.PropertyName == nameof(FileTableViewModel.SelectedRow))
            {
                _selectedFileId = null;
            }
        };

        JobBar.JobPicked += job => OpenRecentScanCommand.Execute(job);
        foreach (var filter in Findings.QuickFilters)
            filter.PropertyChanged += (_, e) =>
            {
                // A quick filter is one of the findings: switching it on shows them.
                if (e.PropertyName == nameof(FindingQuickFilterViewModel.IsActive) && filter.IsActive && !IsFindingsVisible)
                    ShowFindingsCommand.Execute(null);
            };

        OllamaBaseUrl = _ollamaOptions.BaseUrl;
        OllamaKeepAlive = _ollamaOptions.KeepAlive;
        OllamaParallelRequests = _ollamaOptions.MaxParallelRequests.ToString();
        OllamaStatusText = $"Default model: {_ollamaOptions.Model}";

        UpdateCanStartScan();
        _ = InitializeAsync();
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        if (PickFolderDialog == null) return;
        var path = await PickFolderDialog();
        if (!string.IsNullOrWhiteSpace(path))
        {
            RootPath = path;
            CanStartScan = true;
            IsShowingWelcome = false;
            IsShowingPlan = false;
            IsShowingFindings = false;
        }
    }

    private async Task InitializeAsync()
    {
        await RefreshOllamaModelsAsync();
        var all = await _scanJobService.GetAllJobsAsync();
        RecentScans.Clear();
        foreach (var j in all)
            RecentScans.Add(new RecentScanItemViewModel(j));
        IsShowingWelcome = RecentScans.Count > 0;
    }

    [RelayCommand]
    private async Task DeleteRecentScanAsync(ScanJob job)
    {
        await _scanJobService.DeleteJobAsync(job.Id);
        var item = RecentScans.FirstOrDefault(r => r.Job.Id == job.Id);
        if (item != null) RecentScans.Remove(item);
        if (RecentScans.Count == 0) IsShowingWelcome = false;
        if (_activeJobId != job.Id) return;
        _activeJobId = 0;
        _activeJob = null;
        JobViewChanged();
        ShowFindingsCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task OpenRecentScanAsync(ScanJob job)
    {
        SetActiveJob(job);
        RootPath = job.RootPath;
        IsShowingWelcome = false;
        IsShowingPlan = false;
        // What a finished source job holds is the question; a library or a scan under way has no findings to decide.
        IsShowingFindings = job is { Role: Domain.Enums.ScanRole.Source, Status: Domain.Enums.JobStatus.Completed };
        _scanProgress.Apply(job);
        UpdateCanStartScan();
        _ = LoadJobBarAsync();
        var tree = _folderTree.LoadJobAsync(job.Id);
        if (IsShowingFindings) await Findings.LoadAsync(job);
        await tree;
    }

    /// <summary>The findings of the open job, computed again.</summary>
    [RelayCommand(CanExecute = nameof(CanShowFindings))]
    private async Task ShowFindingsAsync()
    {
        if (_activeJob is not { } job) return;
        IsShowingPlan = false;
        IsShowingFindings = true;
        await Findings.LoadAsync(job);
    }

    private bool CanShowFindings() => _activeJob is { Role: Domain.Enums.ScanRole.Source };

    /// <summary>Folder tree and files of the open job.</summary>
    [RelayCommand]
    private void ShowFolders()
    {
        IsShowingPlan = false;
        IsShowingFindings = false;
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    private async Task LoadJobBarAsync()
    {
        try
        {
            await JobBar.LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoadJobBarAsync] {ex}");
        }
    }

    // ── Search ────────────────────────────────────────────────────────────

    partial void OnSearchTextChanged(string value)
    {
        if (_suspendSearch) return;
        _searchCts?.Cancel();
        if (JobSearchService.TermsOf(value).Count == 0)
        {
            EndSearch(reload: true);
            return;
        }
        var cts = _searchCts = new CancellationTokenSource();
        _ = SearchSoonAsync(cts.Token);
    }

    private async Task SearchSoonAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDelay, ct);
            await SearchAsync();
        }
        catch (OperationCanceledException)
        {
            // Typed on.
        }
    }

    /// <summary>Searches the open job now; findings and files show what was found.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        if (_activeJobId <= 0 || JobSearchService.TermsOf(SearchText).Count == 0) return;
        var ct = (_searchCts = new CancellationTokenSource()).Token;
        var (jobId, text) = (_activeJobId, SearchText);
        SearchStatus = "sucht …";
        try
        {
            // Off the UI thread: one pass over every file of the job.
            var result = await Task.Run(() => _searchService.SearchAsync(jobId, text, _exportOptions with { IncludeSensitive = true }, ct: ct), ct);
            if (ct.IsCancellationRequested || jobId != _activeJobId) return;
            _folderBeforeSearch = _folderTree.SelectedItem ?? _folderBeforeSearch;
            _selectedDirectoryPath = string.Empty;
            _selectedFileId = null;
            _folderTree.SelectedItem = null;
            _directory.Clear();
            _fileDetail.Clear();
            _fileTable.ShowSearch(result);
            Findings.ApplySearch(result);
            SearchStatus = PlanDisplay.Files(result.FileIds.Count);
        }
        catch (OperationCanceledException)
        {
            // Another search, or the job was left.
        }
        catch (Exception ex)
        {
            SearchStatus = "Suche fehlgeschlagen: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>Every finding again; with <paramref name="reload"/> the folder shown before the search.</summary>
    private void EndSearch(bool reload)
    {
        _searchCts?.Cancel();
        _suspendSearch = true;
        SearchText = string.Empty;
        _suspendSearch = false;
        SearchStatus = string.Empty;
        Findings.ApplySearch(null);
        var folder = _folderBeforeSearch;
        _folderBeforeSearch = null;
        if (!_fileTable.IsSearch) return;
        _fileTable.Clear();
        _fileDetail.Clear();
        _selectedFileId = null;
        if (reload && folder is not null && _folderTree.SelectedItem is null)
            _folderTree.SelectedItem = folder; // loads its files
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        if (string.IsNullOrWhiteSpace(RootPath)) return;

        IsShowingWelcome = false;
        IsShowingPlan = false;
        IsShowingFindings = false;
        IsScanRunning = true;
        UpdateCanStartScan();
        _scanProgress.StatusText = "Starting…";

        _uiCts = new CancellationTokenSource();

        var job = await _scanJobService.CreateJobAsync(RootPath, _uiCts.Token);
        SetActiveJob(job);
        _scanProgress.Apply(job);
        _ = LoadJobBarAsync();

        await _scanJobService.StartAsync(job.Id, _uiCts.Token);
    }

    [RelayCommand(CanExecute = nameof(CanResumeScan))]
    private async Task ResumeScanAsync()
    {
        if (_activeJobId <= 0) return;

        var job = await _scanJobService.GetJobAsync(_activeJobId);
        if (job == null) return;

        if (job.Status is Domain.Enums.JobStatus.Completed or Domain.Enums.JobStatus.Failed)
        {
            _scanProgress.StatusText = $"Cannot resume job in state {job.Status}.";
            return;
        }

        IsShowingWelcome = false;
        IsScanRunning = true;
        UpdateCanStartScan();

        _uiCts = new CancellationTokenSource();
        await _scanJobService.StartAsync(job.Id, _uiCts.Token);
        _scanProgress.StatusText = "Resuming scan…";
    }

    /// <summary>Another job: nothing of the one before stays in banner, table, detail and export line.</summary>
    private void SetActiveJob(ScanJob job)
    {
        if (_activeJobId != job.Id)
        {
            EndSearch(reload: false);
            Findings.Clear();
            Findings.ResetFiltersCommand.Execute(null);
            _selectedDirectoryPath = string.Empty;
            _selectedFileId = null;
            _fileTable.Clear();
            _fileDetail.Clear();
            _directory.Clear();
            ExportStatusText = string.Empty;
        }
        _activeJobId = job.Id;
        _activeJob = job;
        JobBar.SetActive(job.Id);
        JobViewChanged();
        ShowFindingsCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Writes every file of the open job as CSV, as <c>sfo export</c> does, to a file the person picks.</summary>
    [RelayCommand(CanExecute = nameof(CanExportCsv))]
    private async Task ExportCsvAsync()
    {
        if (PickSaveFileDialog is null || _activeJobId <= 0) return;
        var jobId = _activeJobId;
        var path = await PickSaveFileDialog($"sfo-job-{jobId}.csv");
        if (string.IsNullOrWhiteSpace(path)) return;

        IsExporting = true;
        ExportStatusText = $"CSV-Export läuft: {path}";
        try
        {
            // The disclosure of the settings, as in the CLI: the file leaves the program.
            var rows = await Task.Run(() => _export.ExportJobToFileAsync(jobId, path, _exportOptions));
            ExportStatusText = $"CSV-Export fertig: {PlanDisplay.Files(rows)} in {path}";
        }
        catch (Exception ex)
        {
            ExportStatusText = $"CSV-Export fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    private bool CanExportCsv() => _activeJobId > 0 && !IsExporting;

    /// <summary>Shows the plans of the open job (or of the newest job with a plan).</summary>
    [RelayCommand]
    public async Task OpenPlanAsync(ScanJob? job = null)
    {
        IsShowingPlan = true;
        await Plan.LoadAsync(job?.Id ?? (_activeJobId > 0 ? _activeJobId : null));
    }

    [RelayCommand]
    private void ClosePlan() => IsShowingPlan = false;

    [RelayCommand]
    private async Task BackToHomeAsync()
    {
        IsShowingPlan = false;
        // Pause active work when returning to home screen
        if (IsScanRunning)
        {
            _scanJobService.Pause();
            IsScanRunning = false;
        }

        if (_aiProgress.CanPause)
            _aiCoordinator.Pause();

        _selectedDirectoryPath = string.Empty;
        _selectedFileId = null;

        EndSearch(reload: false);
        _fileTable.Clear();
        _fileDetail.Clear();
        _folderTree.Clear();
        _directory.Clear();
        Findings.Clear();
        IsShowingFindings = false;

        var all = await _scanJobService.GetAllJobsAsync();
        RecentScans.Clear();
        foreach (var j in all)
            RecentScans.Add(new RecentScanItemViewModel(j));

        IsShowingWelcome = true;
        UpdateCanStartScan();
        _scanProgress.StatusText = "Ready.";
    }

    [RelayCommand]
    private void PauseScan()
    {
        _scanJobService.Pause();
        IsScanRunning = false;
        UpdateCanStartScan();
        _scanProgress.StatusText = "Paused";
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanJobService.Cancel();
        IsScanRunning = false;
        UpdateCanStartScan();
        _scanProgress.StatusText = "Cancelled";
    }

    [RelayCommand(CanExecute = nameof(CanPauseAi))]
    private void PauseAi()
    {
        _aiCoordinator.Pause();
    }

    [RelayCommand(CanExecute = nameof(CanResumeAi))]
    private async Task ResumeAiAsync()
    {
        if (_activeJobId <= 0) return;
        await _aiCoordinator.StartOrResumeAsync(_activeJobId);
    }

    [RelayCommand(CanExecute = nameof(CanCancelAi))]
    private void CancelAi()
    {
        _aiCoordinator.Cancel();
    }

    [RelayCommand]
    private async Task LoadJobsAsync()
    {
        var all = await _scanJobService.GetAllJobsAsync();
        Jobs.Clear();
        foreach (var j in all) Jobs.Add(j);
    }

    [RelayCommand]
    private async Task RefreshOllamaModelsAsync()
    {
        IsLoadingOllamaModels = true;
        OllamaStatusText = "Loading local Ollama models…";

        try
        {
            var models = await _ollamaService.GetLocalModelsAsync();
            AvailableOllamaModels.Clear();
            IsScanRunning = false; // This line is modified
            foreach (var model in models)
                AvailableOllamaModels.Add(OllamaModelViewModel.From(model));

            SelectedOllamaModel = AvailableOllamaModels.FirstOrDefault(m =>
                                      string.Equals(m.Name, _ollamaOptions.Model, StringComparison.OrdinalIgnoreCase))
                                  ?? AvailableOllamaModels.FirstOrDefault();

            OllamaStatusText = AvailableOllamaModels.Count > 0
                ? $"Loaded {AvailableOllamaModels.Count} model(s) from Ollama."
                : "No local Ollama models found.";
        }
        catch (Exception ex)
        {
            OllamaStatusText = $"Failed to load Ollama models: {ex.Message}";
        }
        finally
        {
            IsLoadingOllamaModels = false;
        }
    }

    [RelayCommand]
    private async Task SaveOllamaSettingsAsync()
    {
        IsSavingOllamaSettings = true;

        try
        {
            _ollamaOptions.BaseUrl = string.IsNullOrWhiteSpace(OllamaBaseUrl)
                ? _ollamaOptions.BaseUrl
                : OllamaBaseUrl.Trim();
            _ollamaOptions.KeepAlive = string.IsNullOrWhiteSpace(OllamaKeepAlive)
                ? _ollamaOptions.KeepAlive
                : OllamaKeepAlive.Trim();

            if (int.TryParse(OllamaParallelRequests, out var parallel))
            {
                parallel = Math.Clamp(parallel, 1, 8);
                _ollamaOptions.MaxParallelRequests = parallel;
                OllamaParallelRequests = parallel.ToString();
            }
            else
            {
                OllamaParallelRequests = _ollamaOptions.MaxParallelRequests.ToString();
            }

            if (SelectedOllamaModel != null)
                _ollamaOptions.Model = SelectedOllamaModel.Name;

            await _ollamaSettingsService.SaveAsync();

            OllamaStatusText = $"Saved Ollama settings. Active model: {_ollamaOptions.Model}, parallel requests: {_ollamaOptions.MaxParallelRequests}";
        }
        catch (Exception ex)
        {
            OllamaStatusText = $"Saving Ollama settings failed: {ex.Message}";
        }
        finally
        {
            IsSavingOllamaSettings = false;
        }
    }

    [RelayCommand]
    private async Task OpenPromptSettingsAsync()
    {
        if (OpenPromptSettingsDialog == null) return;

        var dialogVm = new AiPromptSettingsViewModel();
        dialogVm.Load(_ollamaOptions);

        var saved = await OpenPromptSettingsDialog(dialogVm);
        if (saved)
        {
            _ollamaOptions.SystemPrompt = dialogVm.SystemPrompt;
            _ollamaOptions.SummaryLanguage = dialogVm.SelectedLanguage;

            await _ollamaSettingsService.SaveAsync();
            OllamaStatusText = $"Prompt settings saved. Language: {_ollamaOptions.SummaryLanguage}";
        }
    }

    [RelayCommand]
    private async Task WarmOllamaModelAsync()
    {
        try
        {
            var modelName = SelectedOllamaModel?.Name ?? _ollamaOptions.Model;
            OllamaStatusText = $"Warming model {modelName}…";
            await _ollamaService.WarmModelAsync(modelName);
            OllamaStatusText = $"Model {modelName} is loaded. Keep-alive: {_ollamaOptions.KeepAlive}";
        }
        catch (Exception ex)
        {
            OllamaStatusText = $"Warm-up failed: {ex.Message}";
        }
    }

    private void OnProgressChanged(object? sender, ScanProgress p)
    {
        // Marshal to UI thread
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _scanProgress.Apply(p);
        });
    }

    private void OnJobStateChanged(object? sender, ScanJob job)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            IsScanRunning = false;
            UpdateCanStartScan();
            ResumeScanCommand.NotifyCanExecuteChanged();
            UpsertJobInCollections(job);
            _scanProgress.Apply(job);
            if (job.Id == _activeJobId) _activeJob = job;
            _ = LoadJobBarAsync();

            // Refresh tree with scanned data
            if (_activeJobId > 0)
                await _folderTree.LoadJobAsync(_activeJobId);

            if (job.Status == Domain.Enums.JobStatus.Completed)
                await _aiCoordinator.StartOrResumeAsync(job.Id);
        });
    }

    private void UpsertJobInCollections(ScanJob job)
    {
        var recentIndex = RecentScans
            .Select((item, index) => (item, index))
            .FirstOrDefault(x => x.item.Job.Id == job.Id)
            .index;

        var recentItem = new RecentScanItemViewModel(job);
        if (recentIndex >= 0 && recentIndex < RecentScans.Count)
            RecentScans[recentIndex] = recentItem;
        else
            RecentScans.Insert(0, recentItem);

        var jobIndex = Jobs
            .Select((existing, index) => (existing, index))
            .FirstOrDefault(x => x.existing.Id == job.Id)
            .index;

        if (jobIndex >= 0 && jobIndex < Jobs.Count)
            Jobs[jobIndex] = job;
        else
            Jobs.Insert(0, job);

        if (SelectedJob?.Id == job.Id)
            SelectedJob = job;
    }

    private async void OnDirectorySelected(object? sender, string path)
    {
        System.Diagnostics.Debug.WriteLine($"[DIR SELECT] _activeJobId={_activeJobId}  path='{path}'");
        if (_activeJobId <= 0) return;
        // A folder chosen in the tree ends a search.
        if (SearchText.Length > 0) EndSearch(reload: false);
        _selectedDirectoryPath = path;
        _selectedFileId = null;
        try
        {
            await _fileTable.LoadAsync(_activeJobId, path);
            await LoadDirectoryAsync(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DIR SELECT ERROR] {ex}");
        }
        _fileDetail.Clear();
    }

    private async Task LoadDirectoryAsync(string path)
    {
        try
        {
            await _directory.LoadAsync(_activeJobId, path, _folderTree.FactsOf(path));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoadDirectoryAsync] {ex}");
            _directory.Clear();
        }
    }

    /// <summary>A person set or took back a verdict: the tree shows it, and the files of the folder follow.</summary>
    private async void OnVerdictChanged(string path, DirectoryVerdict? verdict)
    {
        _folderTree.ApplyVerdict(path, verdict);
        try
        {
            if (_activeJobId > 0 && path == _selectedDirectoryPath)
                await _fileTable.LoadAsync(_activeJobId, path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnVerdictChanged] {ex}");
        }
    }

    private async Task LoadFileDetailAsync(long fileId)
    {
        try
        {
            var file = await _fileQueryService.GetFileByIdAsync(fileId);
            if (file == null) return;
            await _fileDetail.ApplyAsync(file);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoadFileDetailAsync] {ex}");
        }
    }

    private void OnAiProgressChanged(object? sender, AiClassificationProgress progress)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            _aiProgress.Apply(progress);
            await MaybeRefreshActiveDirectoryAsync(force: false);
        });
    }

    private void OnAiStateChanged(object? sender, AiProcessingStateChanged state)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            _aiProgress.Apply(state);
            UpdateCanStartScan();
            PauseAiCommand.NotifyCanExecuteChanged();
            ResumeAiCommand.NotifyCanExecuteChanged();
            CancelAiCommand.NotifyCanExecuteChanged();

            if (state.State is AiProcessingState.Completed or AiProcessingState.Cancelled or AiProcessingState.Failed or AiProcessingState.Unavailable)
            {
                await MaybeRefreshActiveDirectoryAsync(force: true);
                _ = LoadJobBarAsync();
                if (IsFindingsVisible) await Findings.ReloadAsync();
            }
        });
    }

    private async Task MaybeRefreshActiveDirectoryAsync(bool force)
    {
        _ = RefreshFactsAsync(force);
        if (_activeJobId <= 0 || string.IsNullOrWhiteSpace(_selectedDirectoryPath))
            return;

        var now = DateTime.UtcNow;
        if (!force && now - _lastAiRefreshUtc < TimeSpan.FromMilliseconds(750))
            return;

        _lastAiRefreshUtc = now;
        await _fileTable.LoadAsync(_activeJobId, _selectedDirectoryPath);
        await LoadDirectoryAsync(_selectedDirectoryPath);

        if (_selectedFileId is long selectedFileId)
            await LoadFileDetailAsync(selectedFileId);
    }

    /// <summary>Verdicts and sizes in the tree while the analysis runs, and once more when it ends.</summary>
    private async Task RefreshFactsAsync(bool force)
    {
        try
        {
            await (force ? _folderTree.RefreshFactsAsync() : _folderTree.RefreshFactsIfDueAsync());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RefreshFactsAsync] {ex}");
        }
    }

    private void OnReviewApplied(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (!string.IsNullOrWhiteSpace(_selectedDirectoryPath) && _activeJobId > 0)
                await _fileTable.LoadAsync(_activeJobId, _selectedDirectoryPath);
            else if (_fileTable.IsSearch)
                await SearchAsync();
        });
    }

    private bool CanPauseAi() => _aiProgress.CanPause;
    private bool CanResumeAi() => _aiProgress.CanResume;
    private bool CanCancelAi() => _aiProgress.CanCancel;

    private bool CanResumeScan() => _activeJobId > 0 && !IsScanRunning;

    private bool CanRunFocusedRescan() => _activeJobId > 0 && !IsScanRunning && !_aiProgress.IsBusy;

    private async Task RescanDirectoryAsync(string directoryPath)
    {
        if (!CanRunFocusedRescan() || string.IsNullOrWhiteSpace(directoryPath))
        {
            _scanProgress.StatusText = "Rescan is only available while scan and AI are idle.";
            return;
        }

        try
        {
            var progress = new Progress<FocusedRescanProgress>(p => _scanProgress.Apply(p));
            await _focusedRescanService.RescanDirectoryAsync(_activeJobId, directoryPath, progress);

            _selectedDirectoryPath = directoryPath;
            _selectedFileId = null;

            await _folderTree.LoadJobAsync(_activeJobId);
            await _fileTable.LoadAsync(_activeJobId, directoryPath);
            await LoadDirectoryAsync(directoryPath);
            _fileDetail.Clear();

            _scanProgress.StatusText = "Directory rescanned and reanalyzed.";
        }
        catch (Exception ex)
        {
            _scanProgress.StatusText = $"Directory rescan failed: {ex.Message}";
        }
    }

    private async Task ReanalyzeFileAsync(FileRowViewModel row)
    {
        if (!CanRunFocusedRescan())
        {
            _scanProgress.StatusText = "Reanalysis is only available while scan and AI are idle.";
            return;
        }

        try
        {
            var progress = new Progress<FocusedRescanProgress>(p => _scanProgress.Apply(p));
            await _focusedRescanService.ReanalyzeFileAsync(_activeJobId, row.Id, progress);

            var directoryPath = string.IsNullOrWhiteSpace(row.ParentPath)
                ? _selectedDirectoryPath
                : row.ParentPath;

            if (_fileTable.IsSearch)
            {
                await SearchAsync();
                _fileTable.SelectedRow = _fileTable.Rows.FirstOrDefault(r => r.Id == row.Id);
            }
            else if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                _selectedDirectoryPath = directoryPath;
                await _fileTable.LoadAsync(_activeJobId, directoryPath);
                _fileTable.SelectedRow = _fileTable.Rows.FirstOrDefault(r => r.Id == row.Id);
                await LoadDirectoryAsync(directoryPath);
            }

            await LoadFileDetailAsync(row.Id);
            _scanProgress.StatusText = "File reanalyzed.";
        }
        catch (Exception ex)
        {
            _scanProgress.StatusText = $"File reanalysis failed: {ex.Message}";
        }
    }

    private void UpdateCanStartScan()
    {
        CanStartScan = !string.IsNullOrWhiteSpace(RootPath) && !IsScanRunning && !_aiProgress.IsBusy;
        ResumeScanCommand.NotifyCanExecuteChanged();
    }
}
