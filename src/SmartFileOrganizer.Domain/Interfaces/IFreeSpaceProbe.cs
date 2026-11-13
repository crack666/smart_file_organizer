namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>
/// How much room a target has (https://github.com/crack666/smart_file_organizer/issues/74). Only asks the file system;
/// writes nothing and never fails: whatever goes wrong gives null, which the outputs show as <c>unknown</c>.
/// </summary>
public interface IFreeSpaceProbe
{
    /// <summary>
    /// Bytes the current user can still write on the file system that holds <paramref name="path"/> (a local folder,
    /// a mounted share or a network path); null when it cannot be determined. A path that does not exist yet is
    /// answered for its nearest existing parent.
    /// </summary>
    long? AvailableBytes(string path);
}
