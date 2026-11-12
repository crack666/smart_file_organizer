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

    /// <summary>Markers the local model reported in the same classification call.
    /// Stored as tokens in <c>ai_results.sensitive_markers</c>.</summary>
    public SensitiveMarker SensitiveMarkers { get; set; }

    /// <summary>Where the result came from; null for rows written before schema step 6 (all judged by the model).
    /// An <see cref="ResultOrigin.Inherited"/> result has <see cref="IsAiResult"/> false and a capped confidence.</summary>
    public ResultOrigin? Origin { get; set; }

    /// <summary>For an inherited result: the directory whose sample it came from.</summary>
    public long? InheritedFromDirectoryNodeId { get; set; }

    /// <summary>For files in a mixed (or sampled uniform) directory: temporary or worth keeping, as the model judged.</summary>
    public KeepHint? KeepHint { get; set; }

    /// <summary>Why the file has its <see cref="KeepHint"/>.</summary>
    public string? KeepReason { get; set; }
}
