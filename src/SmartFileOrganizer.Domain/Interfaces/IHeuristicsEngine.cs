namespace SmartFileOrganizer.Domain.Interfaces;

public enum ScanDecision
{
    /// <summary>Fully recurse into this directory.</summary>
    Descend,
    /// <summary>List direct children only, do not recurse.</summary>
    Shallow,
    /// <summary>Completely skip this directory.</summary>
    Skip
}

/// <summary>
/// The decision for one directory plus a human-readable reason, stored as <c>dir_reason</c>
/// so that every skipped or shallow directory can be traced back to the rule that caused it.
/// </summary>
public readonly record struct DirectoryEvaluation(ScanDecision Decision, string Reason);

public interface IHeuristicsEngine
{
    /// <summary>
    /// Decide how to handle a directory during a scan. Only the part of <paramref name="fullPath"/>
    /// below <paramref name="rootPath"/> is matched against skip rules; folders above the scan root
    /// were chosen by the user and never cause a skip.
    /// </summary>
    DirectoryEvaluation EvaluateDirectory(string rootPath, string fullPath, int depth);

    /// <summary>
    /// Heuristically determine whether a path looks like a user-data path.
    /// </summary>
    bool IsLikelyUserData(string fullPath);
}
