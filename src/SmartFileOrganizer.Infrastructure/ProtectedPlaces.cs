using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Infrastructure;

/// <summary>
/// Places of this machine no plan target may lie in (https://github.com/crack666/smart_file_organizer/issues/11):
/// targets belong in the permanent storage (a library), never in a standard folder of the operating system or on the
/// system drive. On Windows the user profile and the root of the system drive (which holds the profile, the program
/// folders and Windows); elsewhere the home folder and the system trees below <c>/</c>, because <c>/</c> itself holds
/// every mount.
/// </summary>
public sealed class ProtectedPlaces : IProtectedPlaces
{
    private static readonly string[] UnixSystemTrees =
    [
        "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/opt", "/proc", "/root", "/run", "/sbin", "/srv",
        "/sys", "/usr", "/var", "/Applications", "/Library", "/System", "/private", "/Users"
    ];

    public IReadOnlyList<string> Get()
    {
        var places = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                places.Add(path);
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (OperatingSystem.IsWindows())
        {
            Add(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
            Add(Path.GetPathRoot(Environment.SystemDirectory));
        }
        else
        {
            foreach (var tree in UnixSystemTrees)
                Add(tree);
        }
        return places.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
