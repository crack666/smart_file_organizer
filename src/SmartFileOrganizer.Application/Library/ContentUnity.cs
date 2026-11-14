using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Library;

/// <summary>One file of a unit as the unity check sees it: when it was taken and what it shows.</summary>
public sealed record UnityFile(FileNode File, DateTime? Time, IReadOnlyList<FileFact> Facts);

/// <summary>
/// Whether the files of a unit belong together by content (https://github.com/crack666/smart_file_organizer/issues/85).
/// The second real run had "13 videos in the download folder" as one unit: bundled by kind of file, not by content, its
/// description a mixture (a crack in a wall, an interview, ...), and a mixture seems to fit everything. Two files belong
/// together when they share a concrete feature (a word of the scene or activity that is no general one, the same issuer),
/// or when they were taken close together (<c>UnitGapHours</c>) and one of them is not described or both show the same
/// occasion. The parts are the groups this joins. A unit is one whole as long as at most one part holds described files:
/// without descriptions nothing says the files differ.
/// </summary>
public static class ContentUnity
{
    /// <summary>The parts of the unit, each in time order, the earliest part first; one part when the unit is one whole.</summary>
    public static IReadOnlyList<IReadOnlyList<FileNode>> Parts(IReadOnlyList<UnityFile> files, LibraryWords words, double gapHours)
    {
        if (files.Count < 2) return [files.Select(f => f.File).ToList()];
        var ordered = files
            .OrderBy(f => f.Time ?? DateTime.MaxValue)
            .ThenBy(f => f.File.FullPath, StringComparer.Ordinal)
            .ToList();
        var parent = Enumerable.Range(0, ordered.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i) i = parent[i] = parent[parent[i]];
            return i;
        }
        void Union(int a, int b)
        {
            var (x, y) = (Find(a), Find(b));
            if (x != y) parent[Math.Max(x, y)] = Math.Min(x, y);
        }

        var described = ordered.Select(f => f.Facts.Any(x => x.Key.IsDescription() && !string.IsNullOrWhiteSpace(x.Value))).ToArray();
        var occasions = ordered.Select(f => Occasion(f.Facts, words)).ToArray();

        // A concrete feature in common joins two files, however far apart in time.
        var firstWith = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            foreach (var key in Links(ordered[i].Facts, words))
            {
                if (firstWith.TryGetValue(key, out var j)) Union(i, j);
                else firstWith[key] = i;
            }
        }

        // Close in time: together when one is not described, or both show the same occasion.
        var gap = TimeSpan.FromHours(gapHours);
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            if (ordered[i].Time is not { } a || ordered[i + 1].Time is not { } b || b - a > gap) continue;
            if (!described[i] || !described[i + 1] || occasions[i] is { } o && o == occasions[i + 1])
                Union(i, i + 1);
        }

        var groups = Enumerable.Range(0, ordered.Count).GroupBy(Find).Select(g => g.ToList()).ToList();
        if (groups.Count(g => g.Any(i => described[i])) <= 1) return [ordered.Select(f => f.File).ToList()];
        return groups
            .OrderBy(g => g.Min())
            .Select(g => (IReadOnlyList<FileNode>)g.Select(i => ordered[i].File).ToList())
            .ToList();
    }

    /// <summary>The occasion a file shows, unless it is a general one (<c>everyday</c>, <c>none</c>).</summary>
    private static string? Occasion(IReadOnlyList<FileFact> facts, LibraryWords words)
    {
        var value = facts.FirstOrDefault(f => f.Key == FileFactKey.Occasion)?.Value;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = LibraryWords.Normalize(value);
        return words.GeneralFeatures.Contains(normalized) ? null : normalized;
    }

    /// <summary>Concrete features of a file: words of its scene and activity that are no general ones, and its issuer.</summary>
    public static IEnumerable<string> Links(IReadOnlyList<FileFact> facts, LibraryWords words)
    {
        foreach (var f in facts)
        {
            if (string.IsNullOrWhiteSpace(f.Value)) continue;
            if (f.Key == FileFactKey.Issuer)
            {
                yield return "issuer:" + LibraryWords.Normalize(f.Value);
                continue;
            }
            if (f.Key is not (FileFactKey.Scene or FileFactKey.Activity)) continue;
            foreach (var word in f.Value.Split([' ', ',', '-', '/', '(', ')', '.'], StringSplitOptions.RemoveEmptyEntries))
            {
                var w = LibraryWords.Normalize(word);
                if (w.Length >= 4 && !words.GeneralFeatures.Contains(w) && !words.StopWords.Contains(w))
                    yield return "word:" + w;
            }
        }
    }
}
