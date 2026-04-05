using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

public class RecentScanItemViewModel
{
    public ScanJob Job { get; }

    public RecentScanItemViewModel(ScanJob job) => Job = job;

    public string RootPathDisplay => Job.RootPath;

    public string DateDisplay => Job.CreatedAt.ToLocalTime().ToString("g");

    public string StatusDisplay => Job.Status switch
    {
        JobStatus.Completed  => "Scan complete",
        JobStatus.Running    => "Running",
        JobStatus.Paused     => "Paused",
        JobStatus.Cancelled  => "Cancelled",
        JobStatus.Failed     => "Failed",
        _                    => "Created"
    };

    public string FileCountDisplay =>
        $"{Job.TotalFiles:N0} Dateien  •  {Job.TotalDirectories:N0} Ordner";

    public string StatusColor => Job.Status switch
    {
        JobStatus.Completed  => "#4CAF50",
        JobStatus.Running    => "#2196F3",
        JobStatus.Paused     => "#FF9800",
        JobStatus.Cancelled  => "#FF5722",
        JobStatus.Failed     => "#F44336",
        _                    => "#9E9E9E"
    };
}
