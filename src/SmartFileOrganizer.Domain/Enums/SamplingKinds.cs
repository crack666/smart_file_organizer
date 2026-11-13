using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// Where a file result came from. Stored as code in <c>ai_results.origin</c>; see <see cref="VerdictCodes"/>.
/// Only <see cref="Inherited"/> is derived without looking at the file itself.
/// </summary>
public enum ResultOrigin
{
    /// <summary>A deterministic rule decided.</summary>
    [JsonStringEnumMemberName("rule")] Rule,

    /// <summary>The local model judged the file itself (on its own or as a sample member).</summary>
    [JsonStringEnumMemberName("model")] Model,

    /// <summary>Not looked at: category and target were taken over from the confirmed sample of its directory.</summary>
    [JsonStringEnumMemberName("inherited")] Inherited,

    /// <summary>A person set the result.</summary>
    [JsonStringEnumMemberName("human")] Human,

    /// <summary>A steering agent set the result through the command line.</summary>
    [JsonStringEnumMemberName("agent")] Agent,

    /// <summary>Imported from another tool.</summary>
    [JsonStringEnumMemberName("external")] External,

    /// <summary>
    /// Taken over from the content store: the same content (full SHA-256 and size) was judged before, in this or another
    /// job, by the same model with the same prompt version; no model call was made for this file
    /// (https://github.com/crack666/smart_file_organizer/issues/38). Also the origin of a person's correction carried
    /// over to another file with the same content (<c>user_overrides.origin</c>).
    /// </summary>
    [JsonStringEnumMemberName("reused")] Reused
}

/// <summary>Whether a file in a mixed directory is worth keeping. Stored in <c>ai_results.keep_hint</c>.</summary>
public enum KeepHint
{
    /// <summary>Installer, download remainder, duplicate or other file that can be recreated.</summary>
    [JsonStringEnumMemberName("temporary")] Temporary,

    /// <summary>Personal or hard to replace.</summary>
    [JsonStringEnumMemberName("worth_keeping")] WorthKeeping
}

/// <summary>State of the sample of a uniform directory. Stored in <c>directory_samples.state</c>.</summary>
public enum SampleState
{
    /// <summary>Drawn; the other files wait (status <see cref="FileNodeStatus.Deferred"/>) for its result.</summary>
    [JsonStringEnumMemberName("open")] Open,

    /// <summary>The sample agreed; the other files inherited its result.</summary>
    [JsonStringEnumMemberName("confirmed")] Confirmed,

    /// <summary>The sample disagreed; the directory is now mixed and every file is judged on its own.</summary>
    [JsonStringEnumMemberName("escalated")] Escalated
}

/// <summary>Why a file is part of a sample.</summary>
public enum SampleRole
{
    /// <summary>Drawn from its extension group, in proportion to the group size.</summary>
    [JsonStringEnumMemberName("stratum")] Stratum,

    /// <summary>Smallest or largest file, or far away from the median size.</summary>
    [JsonStringEnumMemberName("size")] Size,

    /// <summary>Oldest or newest file.</summary>
    [JsonStringEnumMemberName("date")] Date,

    /// <summary>Named as anomalous by the directory pre-assessment.</summary>
    [JsonStringEnumMemberName("anomaly")] Anomaly,

    /// <summary>Marked as sensitive; such a file never inherits and is always judged on its own.</summary>
    [JsonStringEnumMemberName("sensitive")] Sensitive
}
