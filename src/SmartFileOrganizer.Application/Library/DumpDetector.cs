using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Library;

/// <summary>
/// Finds device, computer and package dumps in a library and marks their subtrees
/// (<see cref="LibraryNode.Dump"/>). Runs before every other rule: a few dumps hold most of the empty, deep
/// and odd-typed folders of a library and would otherwise drown the real findings.
/// </summary>
public static class DumpDetector
{
    private const string AndroidFolder = "android";

    /// <summary>Marks every dump root and its subtree. Returns the dump roots, outermost only.</summary>
    public static IReadOnlyList<LibraryNode> Mark(LibraryTree tree, LibraryWords words)
    {
        var candidates = new List<(LibraryNode Node, DumpKind Kind, string Reason)>();
        foreach (var node in tree.All)
        {
            if (Detect(node, words) is { } hit)
                candidates.Add(hit);
        }

        var roots = new List<LibraryNode>();
        foreach (var (node, kind, reason) in candidates.OrderBy(c => c.Node.Depth).ThenBy(c => c.Node.FullPath, StringComparer.Ordinal))
        {
            if (node.InDump) continue;   // inside an outer dump already
            node.Dump = node;
            node.DumpKind = kind;
            node.DumpReason = reason;
            foreach (var d in node.Descendants())
                d.Dump = node;
            roots.Add(node);
        }
        return roots;
    }

    private static (LibraryNode Node, DumpKind Kind, string Reason)? Detect(LibraryNode node, LibraryWords words)
    {
        var childNames = node.Children.Select(c => LibraryWords.Normalize(c.Name)).ToList();

        var device = childNames.Where(words.DeviceMarkers.Contains).Distinct().ToList();
        if (device.Contains("lost.dir") || device.Count >= 2)
            return (node, DumpKind.Device, $"holds {Join(node, device)}");

        var packages = node.Children.Where(c => words.AppPackagePattern.IsMatch(c.Name)).ToList();
        if (packages.Count >= words.AppPackageMinChildren)
        {
            // Android/data/com.example.app: the dump is the storage that holds "Android".
            var android = node.Name.Equals(AndroidFolder, StringComparison.OrdinalIgnoreCase)
                ? node
                : node.Ancestors().FirstOrDefault(a => a.Name.Equals(AndroidFolder, StringComparison.OrdinalIgnoreCase));
            var root = android?.Parent ?? node;
            return (root, DumpKind.Device, $"{packages.Count} app package folders such as {packages[0].Name} under {node.RelativePath}");
        }

        var computer = childNames.Where(words.ComputerMarkers.Contains).Distinct().ToList();
        if (computer.Count >= 2)
            return (node, DumpKind.Computer, $"holds {Join(node, computer)}");

        var package = childNames.Where(words.PackageMarkers.Contains).Distinct().ToList();
        if (package.Count > 0)
            return (node, DumpKind.Package, $"holds {Join(node, package)}");

        if (node.Files.Count > 0 && node.Profile.UnitMarkers.Count > 0)
            return (node, DumpKind.Package, $"markers {string.Join(", ", node.Profile.UnitMarkers)}");

        return null;
    }

    /// <summary>The original spelling of the matched child names.</summary>
    private static string Join(LibraryNode node, IEnumerable<string> normalized)
    {
        var set = normalized.ToHashSet(StringComparer.Ordinal);
        return string.Join(", ", node.Children.Where(c => set.Contains(LibraryWords.Normalize(c.Name))).Select(c => c.Name));
    }
}
