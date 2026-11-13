using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// What a directory is, and therefore whether it can be handled as a whole.
/// The code in <see cref="JsonStringEnumMemberNameAttribute"/> is the fixed value stored in the
/// database and written to JSON; see <see cref="VerdictCodes"/>. Words for people come from
/// <see cref="CodeLabels"/>; meanings are listed in <c>docs/glossary.md</c>.
/// </summary>
public enum DirectoryVerdictKind
{
    /// <summary>Project, program, game or backup that only makes sense as a whole; never split up file by file.</summary>
    [JsonStringEnumMemberName("unit")] Unit,

    /// <summary>Collection of similar content (photos of one trip, invoices of one year).</summary>
    [JsonStringEnumMemberName("uniform")] Uniform,

    /// <summary>Storage without a common purpose (desktop, downloads, misc).</summary>
    [JsonStringEnumMemberName("mixed")] Mixed,

    /// <summary>Contains no files, neither directly nor in subdirectories.</summary>
    [JsonStringEnumMemberName("empty")] Empty,

    /// <summary>Operating system, tool or cache directory that is not user content.</summary>
    [JsonStringEnumMemberName("system")] System,

    /// <summary>
    /// Copy of a phone, a computer, a user profile or a removable drive. Recognised as a whole, but not a unit:
    /// its worth-keeping parts are judged like any directory, only its noise is skipped. Decided by a rule only,
    /// never by the model (https://github.com/crack666/smart_file_organizer/issues/59).
    /// </summary>
    [JsonStringEnumMemberName("dump")] Dump
}

/// <summary>Which verdicts the model may give.</summary>
public static class DirectoryVerdictKinds
{
    /// <summary>Verdicts only a rule decides; the model is not offered them, and an answer with one is refused.</summary>
    public static bool IsRuleOnly(this DirectoryVerdictKind kind) => kind == DirectoryVerdictKind.Dump;

    /// <summary>The codes the model may answer, in declaration order.</summary>
    public static IReadOnlyList<string> ModelCodes { get; } =
        Enum.GetValues<DirectoryVerdictKind>().Where(k => !k.IsRuleOnly()).Select(k => k.ToCode()).ToList();
}

/// <summary>How worth keeping the content of a directory is.</summary>
public enum RetentionValue
{
    /// <summary>Personal or hard to replace.</summary>
    [JsonStringEnumMemberName("keep")] Keep,

    /// <summary>Unclear; a human should look.</summary>
    [JsonStringEnumMemberName("review")] Review,

    /// <summary>Can be recreated or downloaded again (installers, caches, temporary files).</summary>
    [JsonStringEnumMemberName("disposable")] Disposable
}

/// <summary>Who decided a directory verdict.</summary>
public enum VerdictSource
{
    /// <summary>A deterministic rule decided without calling the model.</summary>
    [JsonStringEnumMemberName("rule")] Rule,

    /// <summary>The model decided (or was asked and failed).</summary>
    [JsonStringEnumMemberName("model")] Model
}
