using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>One job of the job bar: what was scanned, as what, and how far it is.</summary>
public partial class JobBarItemViewModel(ScanJob job) : ViewModelBase
{
    public ScanJob Job { get; } = job;

    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Job.RootPath)) is { Length: > 0 } n ? n : Job.RootPath;

    public string RoleLabel => CodeLabels.Label(Job.Role, DisplayLanguage.German);
    public string RoleColor => Job.Role == ScanRole.Library ? "#2F6FD6" : "#54606B";

    public string StatusLabel => Job.Status switch
    {
        JobStatus.Completed => "Scan fertig",
        JobStatus.Running => "Scan läuft",
        JobStatus.Paused => "Scan pausiert",
        JobStatus.Cancelled => "Scan abgebrochen",
        JobStatus.Failed => "Scan fehlgeschlagen",
        _ => "angelegt"
    };

    public string StatusColor => Job.Status switch
    {
        JobStatus.Completed => "#8FBF6A",
        JobStatus.Running => "#58A6FF",
        JobStatus.Paused => "#E3B341",
        JobStatus.Failed => "#E5534B",
        _ => "#A0A7B0"
    };

    public string FilesText => $"Job {Job.Id} · {PlanDisplay.Files(Job.TotalFiles)}";

    /// <summary>"beurteilt 120 von 300"; empty until it is read, for a library, and without a file to judge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnalysis))]
    private string _analysisText = string.Empty;

    public bool HasAnalysis => AnalysisText.Length > 0;

    /// <summary>The job the window shows.</summary>
    [ObservableProperty] private bool _isActive;
}

/// <summary>
/// The jobs of the database beside the open one, with role and status, to switch between them
/// (https://github.com/crack666/smart_file_organizer/issues/27). How much of a job is judged is read after the list is
/// shown, one job after the other.
/// </summary>
public partial class JobBarViewModel : ViewModelBase
{
    private readonly ScanJobService _jobs;
    private readonly JobStatusService _status;
    private long _activeJobId;
    private int _version;

    public JobBarViewModel(ScanJobService jobs, JobStatusService status)
    {
        _jobs = jobs;
        _status = status;
    }

    public ObservableCollection<JobBarItemViewModel> Items { get; } = [];

    [ObservableProperty] private JobBarItemViewModel? _selected;

    /// <summary>Raised when the person picks another job than the open one.</summary>
    public event Action<ScanJob>? JobPicked;

    partial void OnSelectedChanged(JobBarItemViewModel? value)
    {
        if (value is not null && value.Job.Id != _activeJobId) JobPicked?.Invoke(value.Job);
    }

    /// <summary>Marks the open job without picking it.</summary>
    public void SetActive(long jobId)
    {
        _activeJobId = jobId;
        foreach (var i in Items) i.IsActive = i.Job.Id == jobId;
        Selected = Items.FirstOrDefault(i => i.IsActive);
    }

    /// <summary>Reads the jobs again, newest first.</summary>
    public async Task LoadAsync()
    {
        var version = ++_version;
        var jobs = await _jobs.GetAllJobsAsync();
        if (version != _version) return;
        var known = Items.ToDictionary(i => i.Job.Id, i => i.AnalysisText);
        Items.Clear();
        foreach (var j in jobs)
            Items.Add(new JobBarItemViewModel(j) { AnalysisText = known.GetValueOrDefault(j.Id, string.Empty) });
        SetActive(_activeJobId);

        foreach (var item in Items.Where(i => i.Job is { Role: ScanRole.Source, Status: JobStatus.Completed }).ToList())
        {
            // Off the UI thread: counts over every file of the job.
            var report = await Task.Run(() => _status.GetAsync(item.Job.Id, maxErrors: 0));
            if (version != _version) return;
            item.AnalysisText = report?.Statistics is { FilesAiEligible: > 0 } s
                ? $"beurteilt {s.FilesAiEligible - s.FilesAiPending - s.FilesAiDeferred} von {s.FilesAiEligible}"
                : string.Empty;
        }
    }
}
