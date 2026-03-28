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

public interface IHeuristicsEngine
{
    /// <summary>
    /// Decide how to handle a directory during a scan.
    /// </summary>
    ScanDecision EvaluateDirectory(string fullPath, int depth);

    /// <summary>
    /// Heuristically determine whether a path looks like a user-data path.
    /// </summary>
    bool IsLikelyUserData(string fullPath);
}
