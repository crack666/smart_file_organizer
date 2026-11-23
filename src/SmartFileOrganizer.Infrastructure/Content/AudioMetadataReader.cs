using System.Text.RegularExpressions;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Infrastructure.Content;

/// <summary>Tags and technical data of an audio file.</summary>
public sealed class AudioMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public int? Year { get; init; }
    public double? DurationSeconds { get; init; }
    public int? Channels { get; init; }
    public int? BitrateKbps { get; init; }
    public string? Format { get; init; }
}

/// <summary>
/// Reads audio tags (ID3, Vorbis comments, MP4 atoms, ...) and duration through ATL (Audio Tools
/// Library), and decides speech recording or music from tags, format and path.
/// </summary>
public static partial class AudioMetadataReader
{
    public static AudioMetadata Read(string path)
    {
        var track = new ATL.Track(path);
        return new AudioMetadata
        {
            Title = Clean(track.Title),
            Artist = Clean(track.Artist),
            Album = Clean(track.Album),
            Genre = Clean(track.Genre),
            Year = track.Year is > 0 and var y ? y : null,
            DurationSeconds = track.DurationMs > 0 ? track.DurationMs / 1000.0 : null,
            Channels = track.ChannelsArrangement?.NbChannels is > 0 and var c ? c : null,
            BitrateKbps = track.Bitrate > 0 ? track.Bitrate : null,
            Format = Clean(track.AudioFormat?.ShortName)
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Formats that phones and recorders use for voice, rarely for music.</summary>
    private static readonly HashSet<string> SpeechExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".amr", ".3ga", ".awb", ".gsm", ".dss", ".ds2" };

    /// <summary>Formats made for audiobooks only (chapters, bookmarks).</summary>
    private static readonly HashSet<string> AudiobookExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".m4b", ".aa", ".aax" };

    /// <summary>An untagged sound up to this many seconds is a sound effect or a sample.</summary>
    public const double SoundEffectMaxSeconds = 3;

    [GeneratedRegex(@"(voice|memo|recording|aufnahme|sprach|diktat|dictation|interview|anruf|call[_ -]|^ptt-|^rec[_ -]?\d|^aud-\d|neue aufnahme)", RegexOptions.IgnoreCase)]
    private static partial Regex SpeechPath();

    [GeneratedRegex(@"(music|musik|mp3s?|songs?|album|playlist|itunes|soundtrack|ost)([\\/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MusicPath();

    [GeneratedRegex(@"(audio ?books?|h(ö|oe)rb(u|ü|ue)ch(er)?|h(ö|oe)rspiele?|spoken ?word)", RegexOptions.IgnoreCase)]
    private static partial Regex AudiobookWords();

    [GeneratedRegex(@"(^|[\\/])podcasts?([\\/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PodcastPath();

    [GeneratedRegex(@"(^|[\\/])(sfx|sounds?|sound ?effects?|soundeffekte?|ger(ä|ae)usche|samples?)([\\/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SoundEffectPath();

    /// <summary>
    /// What an audio file is, with how sure the rule is, or null when tags, format and path do not tell
    /// (https://github.com/crack666/smart_file_organizer/issues/103). In this order:
    /// <list type="number">
    /// <item>an audiobook format (<c>.m4b</c>, <c>.aa</c>, <c>.aax</c>), or an audiobook or podcast genre tag: certain;</item>
    /// <item>a voice format: speech, certain; untagged with voice words in name or folder: speech, likely;</item>
    /// <item>an audiobook, podcast or sound effect folder: likely, before the tags, because audiobooks and podcasts carry
    /// artist and title like music;</item>
    /// <item>a music genre: music, certain; artist with title or album, or a music folder: music, likely;</item>
    /// <item>untagged and at most <see cref="SoundEffectMaxSeconds"/> seconds: sound effect, likely;</item>
    /// <item>untagged mono at 64 kbit/s or less: speech, uncertain.</item>
    /// </list>
    /// </summary>
    public static (AudioKind Kind, ResultCertainty Certainty)? DecideKind(AudioMetadata meta, string fileName, string extension, string relativeDir)
    {
        var tagged = meta.Artist is not null && (meta.Title is not null || meta.Album is not null);
        var genre = meta.Genre;

        if (AudiobookExtensions.Contains(extension)) return (AudioKind.Audiobook, ResultCertainty.Certain);
        if (genre is not null && AudiobookWords().IsMatch(genre)) return (AudioKind.Audiobook, ResultCertainty.Certain);
        if (genre is not null && genre.Contains("podcast", StringComparison.OrdinalIgnoreCase)) return (AudioKind.Podcast, ResultCertainty.Certain);

        if (SpeechExtensions.Contains(extension)) return (AudioKind.Speech, ResultCertainty.Certain);
        if (!tagged && (SpeechPath().IsMatch(fileName) || SpeechPath().IsMatch(relativeDir))) return (AudioKind.Speech, ResultCertainty.Likely);

        if (AudiobookWords().IsMatch(relativeDir)) return (AudioKind.Audiobook, ResultCertainty.Likely);
        if (PodcastPath().IsMatch(relativeDir)) return (AudioKind.Podcast, ResultCertainty.Likely);
        if (SoundEffectPath().IsMatch(relativeDir)) return (AudioKind.SoundEffect, ResultCertainty.Likely);

        if (genre is not null && !genre.Contains("speech", StringComparison.OrdinalIgnoreCase))
            return (AudioKind.Music, ResultCertainty.Certain);
        if (tagged || MusicPath().IsMatch(relativeDir)) return (AudioKind.Music, ResultCertainty.Likely);

        if (meta.Title is null && meta.Artist is null && meta.Album is null && meta.DurationSeconds is <= SoundEffectMaxSeconds and > 0)
            return (AudioKind.SoundEffect, ResultCertainty.Likely);

        if (meta.Channels == 1 && meta.BitrateKbps is <= 64) return (AudioKind.Speech, ResultCertainty.Uncertain);

        return null;
    }
}
