using System.Text;
using System.Text.Json;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Library;

/// <summary>
/// Builds the target structure of <c>sfo library propose</c>: from the model's answer, or, without a model,
/// from the kinds of files the source holds. Photos are named by occasion (<c>{occasion} {year}</c>), the way
/// people find them again, never by year alone.
/// </summary>
public static class TargetProposer
{
    private sealed record Names(string Photos, string Videos, string Music, string Documents, string Archives, string Software, string Backups,
        string PhotoPattern, string VideoPattern, string MusicPattern, string DocumentPattern, string SoftwarePattern, string BackupPattern);

    private static readonly Names English = new("Photos", "Videos", "Music", "Documents", "Archives", "Software", "Device backups",
        "{occasion} {year}", "{title}", "{artist}/{album}", "{area}/{year}", "{name}", "{device} {year}");

    private static readonly Names German = new("Fotos", "Videos", "Musik", "Dokumente", "Archive", "Software", "Geräte-Sicherungen",
        "{Anlass} {Jahr}", "{Titel}", "{Interpret}/{Album}", "{Bereich}/{Jahr}", "{Name}", "{Gerät} {Jahr}");

    public static TargetStructure FromRules(ScanJob job, LibraryTree tree, string? root, string language)
    {
        var n = language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? German : English;
        var t = tree.Root.LiveTypes;
        long Of(FileType type) => t[(int)type];
        var targets = new List<TargetEntry>();

        var images = Of(FileType.Image);
        var videos = Of(FileType.Video);
        var photoExamples = tree.All
            .Where(d => !d.InDump && d.Depth > 0 && d.Files.Count > 0
                        && d.Files.Count(f => f.FileType is (int)FileType.Image or (int)FileType.Video) * 2 > d.Files.Count
                        && NamePattern.IsDated(LibraryText.Classify(d.Name)))
            .OrderByDescending(d => d.Files.Count)
            .Select(d => d.Name)
            .Take(3)
            .ToList();

        if (images > 0)
        {
            var withVideos = videos > 0 && videos <= images;
            targets.Add(new TargetEntry("photos", n.Photos, n.PhotoPattern,
                withVideos ? ["image", "video"] : ["image"],
                $"{images} photos{(withVideos ? $" and {videos} videos" : string.Empty)}; one folder per occasion with its year, "
                + "the way they are found again. Clips of the same occasion stay with its photos.",
                photoExamples));
        }
        if (videos > 0 && videos > images)
            targets.Add(new TargetEntry("videos", n.Videos, n.VideoPattern, ["video"], $"{videos} videos, more than photos; one folder per title.", []));
        if (Of(FileType.Audio) > 0)
            targets.Add(new TargetEntry("music", n.Music, n.MusicPattern, ["audio"], $"{Of(FileType.Audio)} audio files.", []));
        if (Of(FileType.Document) > 0)
            targets.Add(new TargetEntry("documents", n.Documents, n.DocumentPattern, ["document"],
                $"{Of(FileType.Document)} documents; areas of life (home, car, health, work) carry better than persons.", []));
        if (Of(FileType.Archive) > 0)
            targets.Add(new TargetEntry("archives", n.Archives, null, ["archive"], $"{Of(FileType.Archive)} archives, kept as they are.", []));
        var software = Of(FileType.Executable) + Of(FileType.Code) + Of(FileType.Database);
        if (software > 0 || tree.All.Any(d => d.Dump == d && d.DumpKind == DumpKind.Package))
            targets.Add(new TargetEntry("software", n.Software, n.SoftwarePattern, ["executable", "code", "database"],
                "Programs and projects; each is kept whole under its own name.", []));
        if (tree.All.Any(d => d.Dump == d && d.DumpKind is DumpKind.Device or DumpKind.Computer))
            targets.Add(new TargetEntry("device_backups", n.Backups, n.BackupPattern, [],
                "Copies of phones and computers, kept whole until what is worth keeping has been taken out.", []));
        if (targets.Count == 0)
            targets.Add(new TargetEntry("documents", n.Documents, n.DocumentPattern, ["document"], "Nothing recognisable was found; a start.", []));

        return new TargetStructure(TargetStructureFile.SchemaVersion, TargetStructureFile.KindValue, root, job.Id,
            DateTime.UtcNow, AdviceOrigin.Rule, null, targets);
    }

    /// <summary>
    /// Reads <c>{"targets": [{"id", "path", "pattern", "holds", "reason", "examples"}]}</c>. Invalid entries are
    /// dropped and counted; null when none is left.
    /// </summary>
    public static (TargetStructure? Target, int Dropped, string? Error) FromModel(
        ScanJob job, string? content, string? root, string? model, DateTime createdAt)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, 0, "The model returned no content.");

        JsonElement doc;
        try
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start) return (null, 0, "The model's answer holds no JSON object.");
            using var parsed = JsonDocument.Parse(content[start..(end + 1)]);
            doc = parsed.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return (null, 0, $"The model's answer is not valid JSON: {ex.Message}");
        }

        var types = Enum.GetNames<FileType>().Select(x => x.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var targets = new List<TargetEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var dropped = 0;
        if (doc.TryGetProperty("targets", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in array.EnumerateArray())
            {
                var path = Str(e, "path")?.Replace('\\', '/').Trim('/');
                var id = Slug(Str(e, "id") ?? path);
                var entry = new TargetEntry(id, path ?? string.Empty, Str(e, "pattern"),
                    Strings(e, "holds").Select(h => h.ToLowerInvariant()).Where(types.Contains).Distinct().ToList(),
                    Str(e, "reason"), Strings(e, "examples").Take(5).ToList());
                var single = new TargetStructure(TargetStructureFile.SchemaVersion, TargetStructureFile.KindValue, null, null,
                    createdAt, AdviceOrigin.Model, model, [entry]);
                if (TargetStructureFile.Validate(single).Count > 0 || !ids.Add(id))
                {
                    dropped++;
                    continue;
                }
                targets.Add(entry);
            }
        }

        if (targets.Count == 0)
            return (null, dropped, "The model's answer holds no usable target.");
        return (new TargetStructure(TargetStructureFile.SchemaVersion, TargetStructureFile.KindValue, root, job.Id,
            createdAt, AdviceOrigin.Model, model, targets), dropped, null);
    }

    private static string Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder();
        foreach (var c in LibraryWords.Normalize(text))
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? c : '_');
        return sb.ToString().Trim('_');
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0)
            : [];
}
