using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Scanning.Engine;

public class HeuristicsOptions
{
    /// <summary>Directories whose names match any of these patterns are fully skipped.</summary>
    public List<string> SkipPatterns { get; set; } =
    [
        "node_modules", ".git", ".svn", ".hg",
        "bin", "obj", "__pycache__", ".mypy_cache",
        "Windows", "System32", "SysWOW64",
        "$Recycle.Bin", "System Volume Information",
        "AppData",
        "ProgramData\\Microsoft", "ProgramData\\Package Cache",
        "Program Files", "Program Files (x86)",
        ".nuget", "packages",
    ];

    /// <summary>
    /// At this depth or deeper, only list direct children (shallow) unless path looks like user data.
    /// </summary>
    public int DefaultShallowDepth { get; set; } = 6;

    /// <summary>Absolute maximum depth before force-shallow regardless of path heuristics.</summary>
    public int MaxDepth { get; set; } = 12;

    public List<string> UserDataPatterns { get; set; } =
    [
        "Documents", "Pictures", "Videos", "Music", "Desktop",
        "Downloads", "Photos", "Fotos", "Bilder", "Dokumente",
        "Eigene Dateien", "OneDrive", "Dropbox", "Google Drive",
    ];
}

/// <summary>
/// Stateless heuristics engine — evaluates directories against configurable rules.
/// </summary>
public class HeuristicsEngine : IHeuristicsEngine
{
    private readonly HeuristicsOptions _opts;

    public HeuristicsEngine(HeuristicsOptions opts) => _opts = opts;

    public ScanDecision EvaluateDirectory(string fullPath, int depth)
    {
        var name = Path.GetFileName(fullPath);

        // Hidden directories (dotfiles) that aren't known user data
        if (name.StartsWith('.') && !IsLikelyUserData(fullPath))
            return ScanDecision.Skip;

        // Matched skip patterns
        foreach (var pattern in _opts.SkipPatterns)
        {
            if (fullPath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return ScanDecision.Skip;
        }

        // Beyond max depth: always skip
        if (depth >= _opts.MaxDepth)
            return ScanDecision.Skip;

        // Beyond shallow depth, go shallow unless looks like user data
        if (depth >= _opts.DefaultShallowDepth && !IsLikelyUserData(fullPath))
            return ScanDecision.Shallow;

        return ScanDecision.Descend;
    }

    public bool IsLikelyUserData(string fullPath)
    {
        foreach (var pattern in _opts.UserDataPatterns)
        {
            if (fullPath.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
