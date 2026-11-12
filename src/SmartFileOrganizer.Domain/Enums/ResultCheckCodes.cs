using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// How far a file result can be relied on (https://github.com/crack666/smart_file_organizer/issues/23).
/// Stored as code in <c>ai_results.certainty</c>; see <see cref="VerdictCodes"/>. It rests on agreement
/// between independent signals, not on the confidence the model states about itself.
/// </summary>
public enum ResultCertainty
{
    /// <summary>Two independent signals agree: two model calls with different evidence, a rule fact and the
    /// model, a sample member and the confirmed sample of its directory, or an unambiguous rule result.</summary>
    [JsonStringEnumMemberName("certain")] Certain,

    /// <summary>One model answer that nothing contradicts and that the model itself is not unsure about,
    /// an inherited result of a confirmed sample, or a category set by a rule against the model.</summary>
    [JsonStringEnumMemberName("likely")] Likely,

    /// <summary>The result needed a second check that did not happen (switched off, failed, or a person
    /// corrected the file), or a rule decided without being sure.</summary>
    [JsonStringEnumMemberName("uncertain")] Uncertain,

    /// <summary>Two answers disagree, or the model could not say what the file is: a person should look.
    /// Both answers are stored in <c>result_checks</c>.</summary>
    [JsonStringEnumMemberName("review")] Review
}

/// <summary>Why a result was checked a second time. Stored, comma-separated, in <c>result_checks.reasons</c>.</summary>
public enum CheckReason
{
    /// <summary>The model gave no confidence.</summary>
    [JsonStringEnumMemberName("missing_confidence")] MissingConfidence,

    /// <summary>The model's confidence is below <c>SecondCheck:BelowConfidence</c>.</summary>
    [JsonStringEnumMemberName("low_confidence")] LowConfidence,

    /// <summary>The model answered ReviewNeeded or Unknown.</summary>
    [JsonStringEnumMemberName("unclear_category")] UnclearCategory,

    /// <summary>The category does not fit the kind of file the first bytes or the extension name
    /// (a video category for a JPEG).</summary>
    [JsonStringEnumMemberName("kind_mismatch")] KindMismatch,

    /// <summary>An unambiguous metadata rule contradicts the category; the rule wins without a second call.</summary>
    [JsonStringEnumMemberName("rule_disagrees")] RuleDisagrees,

    /// <summary>The file contradicts the retention of its directory verdict (valuable file in a disposable
    /// directory, temporary file in a directory to keep).</summary>
    [JsonStringEnumMemberName("verdict_disagrees")] VerdictDisagrees
}

/// <summary>What a check came to. Stored in <c>result_checks.outcome</c>.</summary>
public enum CheckOutcome
{
    /// <summary>The second answer agrees with the first; the result is <see cref="ResultCertainty.Certain"/>.</summary>
    [JsonStringEnumMemberName("agreed")] Agreed,

    /// <summary>The answers disagree; the result is <see cref="ResultCertainty.Review"/>.</summary>
    [JsonStringEnumMemberName("disagreed")] Disagreed,

    /// <summary>The first answer broke a rule about the kind of file and the second one fits it; the second stands.</summary>
    [JsonStringEnumMemberName("corrected")] Corrected,

    /// <summary>An unambiguous rule set the category against the model; no second call.</summary>
    [JsonStringEnumMemberName("rule_wins")] RuleWins,

    /// <summary>The second call failed; the first answer stays, <see cref="ResultCertainty.Uncertain"/>.</summary>
    [JsonStringEnumMemberName("failed")] Failed,

    /// <summary>A second check was due but not made (switched off, or a person corrected the file).</summary>
    [JsonStringEnumMemberName("skipped")] Skipped
}

/// <summary>How much evidence the input preparer gathers.</summary>
public enum EvidenceDepth
{
    /// <summary>The first look: limits as in https://github.com/crack666/smart_file_organizer/issues/22.</summary>
    Standard,

    /// <summary>The second look: the parts the first evidence named as cut off are loaded further
    /// (more PDF text and pages, more archive names, more video frames).</summary>
    Extended
}
