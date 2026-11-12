using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Scanning.Engine;

public class HeuristicsOptions
{
    /// <summary>
    /// Directories are fully skipped when their path below the scan root contains one of these
    /// patterns as whole segments (case-insensitive). A pattern with separators ("AppData/Local/Temp",
    /// either separator) must match as a run of consecutive segments. "bin" skips "App/bin", not "Robin".
    /// </summary>
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

    public DirectoryEvaluation EvaluateDirectory(string rootPath, string fullPath, int depth)
    {
        var segments = Segments(fullPath);
        var name = segments.Length > 0 ? segments[^1] : string.Empty;

        // Hidden directories (dotfiles) that aren't known user data
        if (name.StartsWith('.') && !IsLikelyUserData(fullPath))
            return new(ScanDecision.Skip, "Skip: hidden directory");

        // Matched skip patterns: whole segments below the scan root only. What lies above the root
        // was chosen by the user and must not hide the scan (e.g. a root inside AppData).
        var relative = BelowRoot(Segments(rootPath), segments);
        foreach (var pattern in _opts.SkipPatterns)
        {
            if (ContainsSegmentRun(relative, Segments(pattern)))
                return new(ScanDecision.Skip, $"Skip: matches skip pattern '{pattern}'");
        }

        // Beyond max depth: always skip
        if (depth >= _opts.MaxDepth)
            return new(ScanDecision.Skip, $"Skip: depth {depth} reaches max depth {_opts.MaxDepth}");

        // Beyond shallow depth, go shallow unless looks like user data
        if (depth >= _opts.DefaultShallowDepth && !IsLikelyUserData(fullPath))
            return new(ScanDecision.Shallow, $"Shallow: depth {depth} reaches shallow depth {_opts.DefaultShallowDepth}");

        return new(ScanDecision.Descend, "Descend");
    }

    /// <remarks>
    /// Evaluates the whole path, including folders above the scan root: a root chosen inside
    /// "Documents" makes everything below it user data. This only ever keeps folders in the scan.
    /// </remarks>
    public bool IsLikelyUserData(string fullPath)
    {
        var segments = Segments(fullPath);
        foreach (var pattern in _opts.UserDataPatterns)
        {
            if (ContainsSegmentRun(segments, Segments(pattern)))
                return true;
        }
        return false;
    }

    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>Splits a path on both separators, independent of the OS the scan runs on.</summary>
    private static string[] Segments(string path) =>
        path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The segments of <paramref name="path"/> below <paramref name="root"/>; the whole
    /// path if it does not lie under the root (stricter, so nothing is wrongly kept).</summary>
    private static ReadOnlySpan<string> BelowRoot(string[] root, string[] path)
    {
        if (root.Length > path.Length)
            return path;
        for (var i = 0; i < root.Length; i++)
        {
            if (!string.Equals(root[i], path[i], StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return path.AsSpan(root.Length);
    }

    /// <summary>True if <paramref name="run"/> occurs as consecutive whole segments.</summary>
    private static bool ContainsSegmentRun(ReadOnlySpan<string> segments, string[] run)
    {
        if (run.Length == 0)
            return false;
        for (var start = 0; start + run.Length <= segments.Length; start++)
        {
            var match = true;
            for (var j = 0; j < run.Length && match; j++)
                match = string.Equals(segments[start + j], run[j], StringComparison.OrdinalIgnoreCase);
            if (match)
                return true;
        }
        return false;
    }
}
