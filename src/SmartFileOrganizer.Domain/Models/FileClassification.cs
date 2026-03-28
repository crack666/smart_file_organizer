using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

public class FileClassification
{
    public long Id { get; set; }
    public long FileNodeId { get; set; }
    public FileCategory Category { get; set; } = FileCategory.Unknown;
    public Importance Importance { get; set; } = Importance.Unknown;
    public double Confidence { get; set; }
    public string? Summary { get; set; }
    public string? SuggestedTarget { get; set; }
    public string? ModelUsed { get; set; }
    public DateTime AnalyzedAt { get; set; }
    public string? RawResponse { get; set; }
    public string? Error { get; set; }
    public bool IsAiResult { get; set; }
}
