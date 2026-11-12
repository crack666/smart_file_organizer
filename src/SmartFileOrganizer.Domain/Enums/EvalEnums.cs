using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// What a stored test case checks. A case is a correction by the human that is kept so later
/// runs can be checked against it. New kinds (directory verdict, finding reported as wrong) are
/// added here when the correction exists in the product.
/// </summary>
public enum EvalCaseKind
{
    /// <summary>The human set another category for a file than the model.</summary>
    [JsonStringEnumMemberName("file_category")] FileCategory
}

/// <summary>Where a test case came from.</summary>
public enum EvalCaseOrigin
{
    /// <summary>A manual override in the review (<c>ReviewService.ApplyOverrideAsync</c>).</summary>
    [JsonStringEnumMemberName("review_override")] ReviewOverride
}

/// <summary>What an evaluation run measured.</summary>
public enum EvalRunMode
{
    /// <summary>A corpus with a truth file was scanned, analysed and compared.</summary>
    [JsonStringEnumMemberName("corpus")] Corpus,

    /// <summary>The stored test cases from corrections were run again.</summary>
    [JsonStringEnumMemberName("cases")] Cases
}

/// <summary>A kind of judgement that an evaluation scores separately.</summary>
public enum EvalKind
{
    [JsonStringEnumMemberName("directory_verdict")] DirectoryVerdict,
    [JsonStringEnumMemberName("file_category")] FileCategory,
    [JsonStringEnumMemberName("sensitive")] Sensitive
}

/// <summary>How one measured item turned out.</summary>
public enum EvalOutcome
{
    /// <summary>Result equals the truth.</summary>
    [JsonStringEnumMemberName("hit")] Hit,

    /// <summary>There is a result and it differs from the truth.</summary>
    [JsonStringEnumMemberName("wrong")] Wrong,

    /// <summary>No result: not scanned, not analysed, or the model call failed.</summary>
    [JsonStringEnumMemberName("missing")] Missing
}
