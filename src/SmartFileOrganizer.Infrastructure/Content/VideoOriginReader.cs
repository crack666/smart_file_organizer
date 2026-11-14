using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SmartFileOrganizer.Application.Media;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>
/// Reads the kind and origin of a video without a model (https://github.com/crack666/smart_file_organizer/issues/103): the
/// container through <see cref="VideoContainerReader"/>, the files next to it (names in the folder, the first kilobytes
/// of an info file or a subtitle file of the same name), then <see cref="VideoKindRules"/>. Reads, never writes; nothing
/// is decoded and no external program runs.
/// </summary>
public sealed partial class VideoOriginReader : IVideoOriginReader
{
    /// <summary>Names of the folder looked at, at most; a folder of thousands of clips needs no more to tell.</summary>
    public const int MaxSiblings = 2000;

    /// <summary>Bytes read of an info or subtitle file.</summary>
    public const int MaxCompanionBytes = 64 * 1024;

    private static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup"];
    private static readonly string[] ArtworkNames = ["poster", "fanart", "folder", "cover", "banner", "landscape", "clearlogo", "thumb", "backdrop"];

    public VideoOriginReading? Read(FileNode file)
    {
        if (string.IsNullOrWhiteSpace(file.FullPath) || !File.Exists(file.FullPath)) return null;

        VideoContainerInfo? container = null;
        try
        {
            container = VideoContainerReader.Read(file.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException
                                       or ArgumentException or InvalidOperationException or MetadataExtractor.ImageProcessingException)
        {
            // An unreadable or broken container: name and companions still tell what they tell.
        }

        var companions = ReadCompanions(file.FullPath);
        var origin = VideoKindRules.Decide(container, file.Name, file.RelativeDir ?? string.Empty, companions);
        return new VideoOriginReading(container, companions, origin);
    }

    /// <summary>What the files next to <paramref name="path"/> say.</summary>
    public static VideoCompanions ReadCompanions(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return VideoCompanions.None;
        var stem = Path.GetFileNameWithoutExtension(path);
        var own = Path.GetFileName(path);

        List<string> names;
        try
        {
            names = Directory.EnumerateFiles(dir).Select(Path.GetFileName).OfType<string>()
                .Where(n => !string.Equals(n, own, StringComparison.OrdinalIgnoreCase))
                .Take(MaxSiblings).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VideoCompanions.None;
        }

        bool SameStem(string n, out string rest)
        {
            rest = string.Empty;
            if (!n.StartsWith(stem, StringComparison.OrdinalIgnoreCase) || n.Length == stem.Length) return false;
            var next = n[stem.Length];
            if (next is not ('.' or '-' or '_')) return false;
            rest = n[stem.Length..];
            return true;
        }

        string? infoRoot = null, infoTitle = null;
        int? infoYear = null, infoSeason = null, infoEpisode = null;
        var nfo = names.FirstOrDefault(n => string.Equals(n, stem + ".nfo", StringComparison.OrdinalIgnoreCase))
                  ?? (names.Contains("movie.nfo", StringComparer.OrdinalIgnoreCase) ? names.First(n => n.Equals("movie.nfo", StringComparison.OrdinalIgnoreCase)) : null);
        if (nfo is not null && Head(Path.Combine(dir, nfo)) is { } xml)
        {
            var root = InfoRoot().Match(xml);
            if (root.Success) infoRoot = root.Groups[1].Value.ToLowerInvariant();
            infoTitle = Element(xml, "showtitle") is { } show && infoRoot == "episodedetails" ? show : Element(xml, "title");
            infoYear = Number(Element(xml, "year")) ?? Number(Element(xml, "premiered")?.Split('-')[0]);
            infoSeason = Number(Element(xml, "season"));
            infoEpisode = Number(Element(xml, "episode"));
        }

        var showInfo = names.Any(n => n.Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase))
                       || File.Exists(Path.Combine(Path.GetDirectoryName(dir) ?? dir, "tvshow.nfo"));

        var telemetry = false;
        var languages = new List<string>();
        foreach (var n in names)
        {
            if (!SameStem(n, out var rest)) continue;
            var ext = Path.GetExtension(n).ToLowerInvariant();
            if (!SubtitleExtensions.Contains(ext)) continue;
            if (ext == ".srt" && Head(Path.Combine(dir, n), 4096) is { } srt && Telemetry().IsMatch(srt))
            {
                telemetry = true;
                continue;
            }
            // Film.de.srt, Film.en.forced.srt, Film-ger.srt: the language is the part between the name and the extension.
            var middle = rest.Length > ext.Length + 1
                ? rest[1..^ext.Length].Split('.', '-', '_').FirstOrDefault(p => p.Length is 2 or 3)
                : null;
            languages.Add(VideoKindRules.Language(middle) ?? "und");
        }

        var artwork = names.Any(n =>
        {
            var s = Path.GetFileNameWithoutExtension(n).ToLowerInvariant();
            var e = Path.GetExtension(n).ToLowerInvariant();
            return e is ".jpg" or ".jpeg" or ".png" or ".tbn"
                   && ArtworkNames.Any(a => s == a || s.EndsWith("-" + a, StringComparison.Ordinal));
        });
        var sideFiles = names.Any(n => SameStem(n, out var rest) && rest.ToLowerInvariant() is ".thm" or ".lrv")
                        || names.Any(n => n.EndsWith(".LRV", StringComparison.OrdinalIgnoreCase) && LrvTwin(n, stem))
                        || names.Any(n => n.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && CameraXml().IsMatch(n[stem.Length..]));
        var download = names.Any(n => SameStem(n, out var rest) && rest.Equals(".info.json", StringComparison.OrdinalIgnoreCase));

        return new VideoCompanions(names, infoRoot, infoTitle, infoYear, infoSeason, infoEpisode, showInfo, telemetry, download, artwork, sideFiles,
            languages.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>GoPro writes the low-resolution copy of <c>GX010042.MP4</c> as <c>GL010042.LRV</c>.</summary>
    private static bool LrvTwin(string lrv, string stem) =>
        stem.Length == 8 && lrv.Length == 12 && stem[0] is 'G' or 'g' && lrv[0] is 'G' or 'g'
        && string.Equals(lrv[2..8], stem[2..8], StringComparison.OrdinalIgnoreCase);

    private static string? Head(string path, int max = MaxCompanionBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(max, (int)Math.Min(int.MaxValue, stream.Length))];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Element(string xml, string name)
    {
        var m = Regex.Match(xml, $@"<{name}\b[^>]*>\s*([^<]+?)\s*</{name}>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static int? Number(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    [GeneratedRegex(@"<\s*(movie|episodedetails|musicvideo|tvshow)\b", RegexOptions.IgnoreCase)]
    private static partial Regex InfoRoot();

    /// <summary>The telemetry drones and action cams write as subtitles: frame counters, exposure, GPS.</summary>
    [GeneratedRegex(@"(SrtCnt|FrameCnt|\[iso\s*:|\[latitude|GPS\s*\(|F/\d+(\.\d+)?,\s*SS|HOME\s*\()", RegexOptions.IgnoreCase)]
    private static partial Regex Telemetry();

    /// <summary>Sony and Canon write a clip's metadata as <c>C0001M01.XML</c> next to <c>C0001.MP4</c>.</summary>
    [GeneratedRegex(@"^M\d{2}\.XML$", RegexOptions.IgnoreCase)]
    private static partial Regex CameraXml();
}
