namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>
/// Whether the root of a library can be read right now (https://github.com/crack666/smart_file_organizer/issues/85): a
/// share that is not connected, a drive that is not plugged in or a folder without access. Only reads the folder's
/// listing; writes nothing.
/// </summary>
public interface ILibraryRootProbe
{
    /// <summary>Null when <paramref name="root"/> can be read; otherwise what is wrong, in one sentence.</summary>
    string? Problem(string root);
}
