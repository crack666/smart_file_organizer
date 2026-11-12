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

    [GeneratedRegex(@"(voice|memo|recording|aufnahme|sprach|diktat|dictation|interview|anruf|call[_ -]|^ptt-|^rec[_ -]?\d|^aud-\d|neue aufnahme)", RegexOptions.IgnoreCase)]
    private static partial Regex SpeechPath();

    [GeneratedRegex(@"(music|musik|mp3s?|songs?|album|playlist|itunes|soundtrack|ost)([\\/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MusicPath();

    /// <summary>
    /// Speech or music, or null when tags, format and path do not tell. Speech: voice formats,
    /// voice words in name or folder, or untagged mono at 64 kbit/s or less. Music: artist with
    /// title or album, a genre, or a music folder.
    /// </summary>
    public static AudioKind? DecideKind(AudioMetadata meta, string fileName, string extension, string relativeDir)
    {
        var tagged = meta.Artist is not null && (meta.Title is not null || meta.Album is not null);

        if (SpeechExtensions.Contains(extension)) return AudioKind.Speech;
        if (!tagged && (SpeechPath().IsMatch(fileName) || SpeechPath().IsMatch(relativeDir))) return AudioKind.Speech;

        if (tagged || (meta.Genre is not null && !meta.Genre.Contains("speech", StringComparison.OrdinalIgnoreCase)
                                              && !meta.Genre.Contains("podcast", StringComparison.OrdinalIgnoreCase)))
            return AudioKind.Music;

        if (MusicPath().IsMatch(relativeDir)) return AudioKind.Music;

        if (meta.Channels == 1 && meta.BitrateKbps is <= 64) return AudioKind.Speech;

        return null;
    }
}
