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
    [JsonStringEnumMemberName("file_category")] FileCategory,

    /// <summary>A finding was reported as wrong (<c>sfo findings wrong</c>): it should not have been found.</summary>
    [JsonStringEnumMemberName("finding")] Finding
}

/// <summary>Where a test case came from.</summary>
public enum EvalCaseOrigin
{
    /// <summary>A manual override in the review (<c>ReviewService.ApplyOverrideAsync</c>).</summary>
    [JsonStringEnumMemberName("review_override")] ReviewOverride,

    /// <summary>A person or a steering agent reported a finding as wrong.</summary>
    [JsonStringEnumMemberName("finding_reported_wrong")] FindingReportedWrong
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
    [JsonStringEnumMemberName("sensitive")] Sensitive,

    /// <summary>The findings of the job against the expected findings of the corpus (<c>findings.json</c>).</summary>
    [JsonStringEnumMemberName("finding")] Finding
}

/// <summary>How one measured item turned out.</summary>
public enum EvalOutcome
{
    /// <summary>Result equals the truth.</summary>
    [JsonStringEnumMemberName("hit")] Hit,

    /// <summary>There is a result and it differs from the truth.</summary>
    [JsonStringEnumMemberName("wrong")] Wrong,

    /// <summary>No result: not scanned, not analysed, or the model call failed.</summary>
    [JsonStringEnumMemberName("missing")] Missing,

    /// <summary>
    /// A finding no expectation asks for: noise in the list. Not part of <c>total</c>; counted on its own
    /// (<see cref="EvalKind.Finding"/> only).
    /// </summary>
    [JsonStringEnumMemberName("superfluous")] Superfluous
}
