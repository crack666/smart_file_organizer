using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

public class ScanJob
{
    public long Id { get; set; }
    public string RootPath { get; set; } = string.Empty;
    public JobStatus Status { get; set; } = JobStatus.Created;
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long TotalFiles { get; set; }
    public long ProcessedFiles { get; set; }
    public long TotalDirectories { get; set; }
    public long ProcessedDirectories { get; set; }
    public int ErrorCount { get; set; }
    public string? LastError { get; set; }

    public double ProgressPercent =>
        TotalFiles > 0 ? (double)ProcessedFiles / TotalFiles * 100.0 : 0;
}
