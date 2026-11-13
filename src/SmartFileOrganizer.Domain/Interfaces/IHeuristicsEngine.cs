using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Interfaces;

public enum ScanDecision
{
    /// <summary>Fully recurse into this directory.</summary>
    Descend,
    /// <summary>List direct files and count subdirectories, but do not recurse.</summary>
    Shallow,
    /// <summary>Completely skip this directory.</summary>
    Skip,
    /// <summary>The directory is a link (junction, symbolic link, mount point) that is not followed: recorded
    /// with its target, not entered, its content not counted.</summary>
    Link
}

/// <summary>
/// The decision for one directory plus a human-readable reason, stored as <c>dir_reason</c>
/// so that every skipped or shallow directory can be traced back to the rule that caused it.
/// </summary>
public readonly record struct DirectoryEvaluation(ScanDecision Decision, string Reason);

/// <summary>The direct files of a directory, counted by type. Used to decide by content.</summary>
public sealed class DirectoryContent
{
    private readonly Dictionary<FileType, int> _counts;

    public DirectoryContent(IEnumerable<FileType> fileTypes)
    {
        _counts = [];
        foreach (var type in fileTypes)
        {
            _counts[type] = _counts.GetValueOrDefault(type) + 1;
            FileCount++;
        }
    }

    public int FileCount { get; }

    public IReadOnlyDictionary<FileType, int> CountsByType => _counts;

    /// <summary>Number of files whose type is one of <paramref name="types"/>.</summary>
    public int Count(IEnumerable<FileType> types) => types.Distinct().Sum(t => _counts.GetValueOrDefault(t));
}

public interface IHeuristicsEngine
{
    /// <summary>
    /// Decide how to handle a directory during a scan. Only the part of <paramref name="fullPath"/>
    /// below <paramref name="rootPath"/> is matched against skip and shallow rules; folders above the
    /// scan root were chosen by the user and never cause a skip.
    /// </summary>
    /// <param name="content">
    /// Lists the direct files of the directory. Called only when the decision depends on the content
    /// (a folder at or beyond the shallow depth that no rule covers). Without it, or when it returns
    /// null, such a folder is descended into, because a folder wrongly kept costs time while a folder
    /// wrongly left out loses data.
    /// </param>
    DirectoryEvaluation EvaluateDirectory(string rootPath, string fullPath, int depth, Func<DirectoryContent?>? content = null);

    /// <summary>
    /// Whether a user-data rule matches anywhere in the path, including folders above the scan root.
    /// </summary>
    bool IsLikelyUserData(string fullPath);
}
