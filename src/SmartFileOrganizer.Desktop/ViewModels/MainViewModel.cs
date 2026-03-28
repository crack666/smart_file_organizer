using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ScanJobService _scanJobService;
    private readonly FileQueryService _fileQueryService;
    private readonly FolderTreeViewModel _folderTree;
    private readonly FileTableViewModel _fileTable;
    private readonly FileDetailViewModel _fileDetail;
    private readonly ScanProgressViewModel _scanProgress;

    private long _activeJobId;
    private CancellationTokenSource? _uiCts;

    // Sub-viewmodels exposed to the view
    public FolderTreeViewModel FolderTree => _folderTree;
    public FileTableViewModel FileTable => _fileTable;
    public FileDetailViewModel FileDetail => _fileDetail;
    public ScanProgressViewModel ScanProgress => _scanProgress;

    [ObservableProperty] private string _rootPath = string.Empty;
    [ObservableProperty] private bool _isScanRunning;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    private bool _canStartScan;
    [ObservableProperty] private ObservableCollection<ScanJob> _jobs = [];
    [ObservableProperty] private ScanJob? _selectedJob;

    // Injected by the view for folder-picker dialog
    public Func<Task<string?>>? PickFolderDialog { get; set; }

    public MainViewModel(
        ScanJobService scanJobService,
        FileQueryService fileQueryService,
        FolderTreeViewModel folderTree,
        FileTableViewModel fileTable,
        FileDetailViewModel fileDetail,
        ScanProgressViewModel scanProgress)
    {
        _scanJobService = scanJobService;
        _fileQueryService = fileQueryService;
        _folderTree = folderTree;
        _fileTable = fileTable;
        _fileDetail = fileDetail;
        _scanProgress = scanProgress;

        _scanJobService.ProgressChanged += OnProgressChanged;
        _scanJobService.JobStateChanged += OnJobStateChanged;

        _folderTree.DirectorySelected += OnDirectorySelected;

        _fileTable.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileTableViewModel.SelectedRow) &&
                _fileTable.SelectedRow != null)
                _ = LoadFileDetailAsync(_fileTable.SelectedRow.Id);
        };
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
        CanStartScan = false;
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
        CanStartScan = true;
        _scanProgress.StatusText = "Paused";
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanJobService.Cancel();
        IsScanRunning = false;
        CanStartScan = !string.IsNullOrWhiteSpace(RootPath);
        _scanProgress.StatusText = "Cancelled";
    }

    [RelayCommand]
    private async Task LoadJobsAsync()
    {
        var all = await _scanJobService.GetAllJobsAsync();
        Jobs.Clear();
        foreach (var j in all) Jobs.Add(j);
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
            CanStartScan = true;
            _scanProgress.Apply(job);

            // Refresh tree with scanned data
            if (_activeJobId > 0)
                await _folderTree.LoadJobAsync(_activeJobId);
        });
    }

    private async void OnDirectorySelected(object? sender, string path)
    {
        System.Diagnostics.Debug.WriteLine($"[DIR SELECT] _activeJobId={_activeJobId}  path='{path}'");
        if (_activeJobId <= 0) return;
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
}
