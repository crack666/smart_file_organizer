using CommunityToolkit.Mvvm.ComponentModel;
using SmartFileOrganizer.Application.Services;

namespace SmartFileOrganizer.Desktop.ViewModels;

public partial class AiProgressViewModel : ViewModelBase
{
    [ObservableProperty] private long _jobId;
    [ObservableProperty] private string _statusText = "AI idle";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private long _processedFiles;
    [ObservableProperty] private long _totalFiles;
    [ObservableProperty] private string _currentPath = string.Empty;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;

    public bool CanPause => IsRunning;
    public bool CanResume => IsPaused;
    public bool CanCancel => IsRunning || IsPaused;
    public bool IsBusy => IsRunning || IsPaused;

    public void Apply(AiClassificationProgress progress)
    {
        JobId = progress.JobId;
        ProcessedFiles = progress.ProcessedFiles;
        TotalFiles = progress.TotalFiles;
        CurrentPath = progress.CurrentPath;
        ErrorCount = progress.ErrorCount;
        StatusText = progress.StatusText;
        ProgressPercent = progress.TotalFiles > 0
            ? (double)progress.ProcessedFiles / progress.TotalFiles * 100.0
            : 0;
        OnStateFlagsChanged();
    }

    public void Apply(AiProcessingStateChanged state)
    {
        JobId = state.JobId;
        StatusText = state.ErrorMessage is { Length: > 0 }
            ? $"{state.StatusText} ({state.ErrorMessage})"
            : state.StatusText;

        IsRunning = state.State is AiProcessingState.Starting or AiProcessingState.Running;
        IsPaused = state.State == AiProcessingState.Paused;

        if (state.State is AiProcessingState.Cancelled or AiProcessingState.Failed or AiProcessingState.Unavailable)
            CurrentPath = string.Empty;

        if (state.State == AiProcessingState.Completed && TotalFiles > 0)
            ProgressPercent = 100;

        OnStateFlagsChanged();
    }

    private void OnStateFlagsChanged()
    {
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(IsBusy));
    }
}