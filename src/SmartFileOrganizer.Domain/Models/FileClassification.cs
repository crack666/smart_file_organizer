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

    /// <summary>What the evidence held and lacked when this result was made, as JSON
    /// (<c>{"contains":[...],"missing":[...]}</c>). Stored in <c>ai_results.evidence</c>, so a wrong
    /// result can be traced to thin evidence or to a wrong judgement.</summary>
    public string? Evidence { get; set; }

    /// <summary>How far the result can be relied on; null for rows written before schema step 10.
    /// Stored as code in <c>ai_results.certainty</c>.</summary>
    public ResultCertainty? Certainty { get; set; }

    /// <summary>How often the file went through Phase 2 (a retry of a failed file counts; the second
    /// call of a check does not). Stored in <c>ai_results.attempts</c>; the database counts it.</summary>
    public int Attempts { get; set; } = 1;

    /// <summary>Error of the attempt before this one, kept when a retry replaces a failed result.</summary>
    public string? PreviousError { get; set; }

    /// <summary>Set by the parser when the model gave no confidence; <see cref="Confidence"/> is then 0.
    /// Not stored: the check reason <c>missing_confidence</c> records it.</summary>
    public bool ConfidenceMissing { get; set; }

    /// <summary>
    /// What the model described in the same call (stage 1 of https://github.com/crack666/smart_file_organizer/issues/37):
    /// scene, occasion, people of a picture, kind, issuer and year of a document; source model, codes where the
    /// value is fixed. Not a column of <c>ai_results</c>: they are written to <c>file_facts</c>, never for a marked file.
    /// </summary>
    public List<FileFact> DescribedFacts { get; set; } = [];
}
