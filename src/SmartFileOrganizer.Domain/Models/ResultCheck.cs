using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// One check of a file result (https://github.com/crack666/smart_file_organizer/issues/23): why it was
/// checked, both answers as they came, and the outcome. One row per check in <c>result_checks</c>; rows are
/// only added, never changed, so no answer is lost when the result in <c>ai_results</c> changes.
/// </summary>
public sealed class ResultCheck
{
    public long Id { get; set; }
    public long FileNodeId { get; set; }
    public IReadOnlyList<CheckReason> Reasons { get; set; } = [];
    public CheckOutcome Outcome { get; set; }

    /// <summary>For <see cref="CheckOutcome.RuleWins"/> and kind mismatches: what the rule said.</summary>
    public string? RuleNote { get; set; }

    /// <summary>The first model answer, unchanged.</summary>
    public CheckAnswer First { get; set; } = new();

    /// <summary>The second model answer (more evidence, directory context), null when none was made.</summary>
    public CheckAnswer? Second { get; set; }

    public DateTime CheckedAt { get; set; }
}

/// <summary>One model answer as stored in a check (JSON in <c>first_answer</c> / <c>second_answer</c>).</summary>
public sealed class CheckAnswer
{
    public FileCategory Category { get; set; }
    public Importance Importance { get; set; }

    /// <summary>Null when the model stated none.</summary>
    public double? Confidence { get; set; }

    public string? Summary { get; set; }
    public string? SuggestedTarget { get; set; }
    public KeepHint? KeepHint { get; set; }
    public string? KeepReason { get; set; }
    public SensitiveMarker SensitiveMarkers { get; set; }
    public string? ModelUsed { get; set; }
    public DateTime AnalyzedAt { get; set; }
    public string? RawResponse { get; set; }
    public string? Evidence { get; set; }
    public string? Error { get; set; }

    public static CheckAnswer From(FileClassification c) => new()
    {
        Category = c.Category,
        Importance = c.Importance,
        Confidence = c.ConfidenceMissing ? null : c.Confidence,
        Summary = c.Summary,
        SuggestedTarget = c.SuggestedTarget,
        KeepHint = c.KeepHint,
        KeepReason = c.KeepReason,
        SensitiveMarkers = c.SensitiveMarkers,
        ModelUsed = c.ModelUsed,
        AnalyzedAt = c.AnalyzedAt,
        RawResponse = c.RawResponse,
        Evidence = c.Evidence,
        Error = c.Error
    };
}
