using CommunityToolkit.Mvvm.ComponentModel;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// Represents a single row in the scan-job list / sidebar.
/// </summary>
public partial class ScanProgressViewModel : ViewModelBase
{
    [ObservableProperty] private long _jobId;
    [ObservableProperty] private string _rootPath = string.Empty;
    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private long _processedFiles;
    [ObservableProperty] private long _totalFiles;
    [ObservableProperty] private string _currentPath = string.Empty;
    [ObservableProperty] private int _errorCount;

    public void Apply(ScanProgress p)
    {
        JobId = p.JobId;
        ProcessedFiles = p.ProcessedFiles;
        TotalFiles = p.TotalFiles;
        CurrentPath = p.CurrentPath;
        ErrorCount = p.ErrorCount;
        ProgressPercent = p.TotalFiles > 0
            ? (double)p.ProcessedFiles / p.TotalFiles * 100.0
            : 0;
        StatusText = $"Scanning… {p.ProcessedFiles:N0} / {(p.TotalFiles > 0 ? p.TotalFiles.ToString("N0") : "?")}";
    }

    public void Apply(ScanJob job)
    {
        JobId = job.Id;
        RootPath = job.RootPath;
        StatusText = job.Status.ToString();
        ProgressPercent = job.ProgressPercent;
        ProcessedFiles = job.ProcessedFiles;
        TotalFiles = job.TotalFiles;
    }
}
