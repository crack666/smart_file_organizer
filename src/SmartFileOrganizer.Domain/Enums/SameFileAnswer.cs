using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// Answer to "do these two paths mean the same file?"
/// (<see cref="Interfaces.IFileSystemAccessor.SameFile"/>). The execution of a plan removes an original only
/// when the answer is <see cref="Different"/>; <see cref="Unknown"/> never counts as different.
/// </summary>
public enum SameFileAnswer
{
    /// <summary>Both paths lead to the same file: equal path, a link, a hard link, or the same volume reached twice.</summary>
    [JsonStringEnumMemberName("same")]
    Same,

    /// <summary>Both files were found and are proven to be two files.</summary>
    [JsonStringEnumMemberName("different")]
    Different,

    /// <summary>It could not be decided: a path is missing or unreadable, or the file system gives no file identity.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown
}
