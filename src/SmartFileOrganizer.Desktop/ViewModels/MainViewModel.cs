using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Desktop.Services;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
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

    private long _activeJobId;
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

    [ObservableProperty] private string _rootPath = string.Empty;
    [ObservableProperty] private bool _isScanRunning;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    private bool _canStartScan;
    [ObservableProperty] private ObservableCollection<ScanJob> _jobs = [];
    [ObservableProperty] private ScanJob? _selectedJob;
    [ObservableProperty] private ObservableCollection<OllamaModelViewModel> _availableOllamaModels = [];
    [ObservableProperty] private OllamaModelViewModel? _selectedOllamaModel;
    [ObservableProperty] private string _ollamaBaseUrl = string.Empty;
    [ObservableProperty] private string _ollamaKeepAlive = string.Empty;
    [ObservableProperty] private string _ollamaStatusText = "Ollama settings loaded.";
    [ObservableProperty] private bool _isLoadingOllamaModels;
    [ObservableProperty] private bool _isSavingOllamaSettings;

    // Injected by the view for folder-picker dialog
    public Func<Task<string?>>? PickFolderDialog { get; set; }

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
        AiProgressViewModel aiProgress)
    {
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

        _scanJobService.ProgressChanged += OnProgressChanged;
        _scanJobService.JobStateChanged += OnJobStateChanged;
        _aiCoordinator.ProgressChanged += OnAiProgressChanged;
        _aiCoordinator.StateChanged += OnAiStateChanged;

        _folderTree.DirectorySelected += OnDirectorySelected;

        _fileTable.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileTableViewModel.SelectedRow) &&
                _fileTable.SelectedRow != null)
            {
                _selectedFileId = _fileTable.SelectedRow.Id;
                _ = LoadFileDetailAsync(_fileTable.SelectedRow.Id);
            }
            else if (e.PropertyName == nameof(FileTableViewModel.SelectedRow))
            {
                _selectedFileId = null;
            }
        };

        OllamaBaseUrl = _ollamaOptions.BaseUrl;
        OllamaKeepAlive = _ollamaOptions.KeepAlive;
        OllamaStatusText = $"Default model: {_ollamaOptions.Model}";

        UpdateCanStartScan();
        _ = RefreshOllamaModelsAsync();
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
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        if (string.IsNullOrWhiteSpace(RootPath)) return;

        IsScanRunning = true;
        UpdateCanStartScan();
        _scanProgress.StatusText = "Starting…";

        _uiCts = new CancellationTokenSource();

        var job = await _scanJobService.CreateJobAsync(RootPath, _uiCts.Token);
        _activeJobId = job.Id;
        _scanProgress.Apply(job);

        await _scanJobService.StartAsync(job.Id, _uiCts.Token);
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

            if (SelectedOllamaModel != null)
                _ollamaOptions.Model = SelectedOllamaModel.Name;

            await _ollamaSettingsService.SaveAsync();

            OllamaStatusText = $"Saved Ollama settings. Active model: {_ollamaOptions.Model}";
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
            _scanProgress.Apply(job);

            // Refresh tree with scanned data
            if (_activeJobId > 0)
                await _folderTree.LoadJobAsync(_activeJobId);

            if (job.Status == Domain.Enums.JobStatus.Completed)
                await _aiCoordinator.StartOrResumeAsync(job.Id);
        });
    }

    private async void OnDirectorySelected(object? sender, string path)
    {
        System.Diagnostics.Debug.WriteLine($"[DIR SELECT] _activeJobId={_activeJobId}  path='{path}'");
        if (_activeJobId <= 0) return;
        _selectedDirectoryPath = path;
        try
        {
            await _fileTable.LoadAsync(_activeJobId, path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DIR SELECT ERROR] {ex}");
        }
        _fileDetail.Clear();
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
                await MaybeRefreshActiveDirectoryAsync(force: true);
        });
    }

    private async Task MaybeRefreshActiveDirectoryAsync(bool force)
    {
        if (_activeJobId <= 0 || string.IsNullOrWhiteSpace(_selectedDirectoryPath))
            return;

        var now = DateTime.UtcNow;
        if (!force && now - _lastAiRefreshUtc < TimeSpan.FromMilliseconds(750))
            return;

        _lastAiRefreshUtc = now;
        await _fileTable.LoadAsync(_activeJobId, _selectedDirectoryPath);

        if (_selectedFileId is long selectedFileId)
            await LoadFileDetailAsync(selectedFileId);
    }

    private bool CanPauseAi() => _aiProgress.CanPause;
    private bool CanResumeAi() => _aiProgress.CanResume;
    private bool CanCancelAi() => _aiProgress.CanCancel;

    private void UpdateCanStartScan()
    {
        CanStartScan = !string.IsNullOrWhiteSpace(RootPath) && !IsScanRunning && !_aiProgress.IsBusy;
    }
}
