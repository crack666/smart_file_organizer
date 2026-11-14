using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Media;

/// <summary>
/// Splits the files of a finding by the kind of their videos (https://github.com/crack666/smart_file_organizer/issues/103):
/// a film, the episodes of a series and own holiday clips in one folder belong to three places, whatever their frames
/// look like. Only kinds read as at least likely count. Files without such a kind (pictures, unknown videos) go with
/// the own recordings when there are any, else with the largest part, as they are most often the same occasion.
/// </summary>
public static class VideoKindSplit
{
    /// <summary>The parts by kind, largest first; one part when fewer than two kinds are known.</summary>
    public static IReadOnlyList<IReadOnlyList<FileNode>> Parts(IReadOnlyList<FileNode> files, IReadOnlyDictionary<long, IReadOnlyList<FileFact>> facts)
    {
        var byKind = new Dictionary<string, List<FileNode>>(StringComparer.Ordinal);
        var rest = new List<FileNode>();
        foreach (var file in files)
        {
            var kind = facts.TryGetValue(file.Id, out var list) ? list.FirstOrDefault(f => f.Key == FileFactKey.VideoKind) : null;
            if (kind is null || kind.Value == VideoKind.Unknown.ToCode() || kind.Certainty is not (ResultCertainty.Certain or ResultCertainty.Likely))
            {
                rest.Add(file);
                continue;
            }
            if (!byKind.TryGetValue(kind.Value, out var part)) byKind[kind.Value] = part = [];
            part.Add(file);
        }
        if (byKind.Count < 2) return [files];

        var home = byKind.GetValueOrDefault(VideoKind.OwnRecording.ToCode()) ?? byKind.Values.OrderByDescending(p => p.Count).First();
        home.AddRange(rest);
        // Keep the order of the files within a part; order the parts largest first, then by kind code for a stable answer.
        var order = new Dictionary<FileNode, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < files.Count; i++) order.TryAdd(files[i], i);
        return byKind
            .OrderByDescending(p => p.Value.Count).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (IReadOnlyList<FileNode>)p.Value.OrderBy(f => order[f]).ToList())
            .ToList();
    }
}
