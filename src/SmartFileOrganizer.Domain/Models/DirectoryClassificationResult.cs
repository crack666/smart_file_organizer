namespace SmartFileOrganizer.Domain.Models;

public class DirectoryClassificationResult
{
    public long Id { get; set; }
    public long DirectoryNodeId { get; set; }
    /// <summary>"pre_assessment" or "summary"</summary>
    public string Phase { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? Theme { get; set; }
    /// <summary>"high", "medium", or "low"</summary>
    public string? Homogeneity { get; set; }
    public string? DominantType { get; set; }
    /// <summary>
    /// Pre-assessment: JSON [{name, reason}] from LLM, later resolved to [{id, name, reason}].
    /// Summary phase: JSON [{name, reason}] of confirmed anomalies.
    /// </summary>
    public string? AnomalousFileIds { get; set; }
    /// <summary>"analyze_all", "random_sample", or "skip"</summary>
    public string? SamplingStrategy { get; set; }
    public int SampleSize { get; set; }
    public string? ModelUsed { get; set; }
    public DateTime AnalyzedAt { get; set; }
    public string? Error { get; set; }
}
