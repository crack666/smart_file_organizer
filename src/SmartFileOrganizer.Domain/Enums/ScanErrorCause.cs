using System.Security;
using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// Why the scan could not read a folder or a file. Stored as the code in
/// <c>directory_nodes.scan_error_cause</c> and <c>file_nodes.scan_error_cause</c>; <c>sfo status</c>
/// groups the errors by it (https://github.com/crack666/smart_file_organizer/issues/57).
/// </summary>
public enum ScanErrorCause
{
    /// <summary>The account running the scan may not read it.</summary>
    [JsonStringEnumMemberName("access_denied")]
    AccessDenied,

    /// <summary>It was gone when the scan came to read it.</summary>
    [JsonStringEnumMemberName("not_found")]
    NotFound,

    /// <summary>The path is longer than the system accepts.</summary>
    [JsonStringEnumMemberName("path_too_long")]
    PathTooLong,

    /// <summary>Any other read error of the file system or the device.</summary>
    [JsonStringEnumMemberName("io_error")]
    IoError
}

public static class ScanErrorCauses
{
    /// <summary>The cause of a read error. Everything that is not one of the specific causes is <see cref="ScanErrorCause.IoError"/>.</summary>
    public static ScanErrorCause Of(Exception error) => error switch
    {
        UnauthorizedAccessException or SecurityException => ScanErrorCause.AccessDenied,
        DirectoryNotFoundException or FileNotFoundException or DriveNotFoundException => ScanErrorCause.NotFound,
        PathTooLongException => ScanErrorCause.PathTooLong,
        _ => ScanErrorCause.IoError
    };

    /// <summary>Whether the exception is a read error of the file system, as opposed to a bug.</summary>
    public static bool IsReadError(Exception error) =>
        error is IOException or UnauthorizedAccessException or SecurityException;
}
