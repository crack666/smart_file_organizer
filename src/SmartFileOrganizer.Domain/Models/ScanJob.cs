using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

public class ScanJob
{
    public long Id { get; set; }
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Set when the job is created and never changed afterwards.</summary>
    public ScanRole Role { get; set; } = ScanRole.Quelle;

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

    // The scan rules (profile) of this job; null for jobs created before rules were stored.
    public string? RulesName { get; set; }
    public string? RulesSource { get; set; }
    public string? RulesHash { get; set; }
    public string? RulesJson { get; set; }

    /// <summary>The stored rules, or null if none are recorded yet.</summary>
    public ScanRulesProfile? Rules
    {
        get => RulesJson == null ? null : new ScanRulesProfile(RulesName ?? string.Empty, RulesSource ?? string.Empty, RulesHash ?? string.Empty, RulesJson);
        set
        {
            RulesName = value?.Name;
            RulesSource = value?.Source;
            RulesHash = value?.Hash;
            RulesJson = value?.Json;
        }
    }

    public double ProgressPercent =>
        TotalFiles > 0 ? (double)ProcessedFiles / TotalFiles * 100.0 : 0;
}
