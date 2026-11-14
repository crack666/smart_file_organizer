using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Media;

/// <summary>
/// The kind and origin of one video as the read side shows it (<c>sfo files</c>, <c>sfo show</c>, findings candidates):
/// codes, the source and certainty of the kind, and for published works title, year, season and episode
/// (https://github.com/crack666/smart_file_organizer/issues/103).
/// </summary>
/// <param name="Kind">A video kind code.</param>
/// <param name="Source">A fact source code: where the kind was read.</param>
/// <param name="Certainty">A result certainty code.</param>
/// <param name="Device">A recording device code.</param>
/// <param name="Title">Title of a film, series or music video; null for a protected file.</param>
/// <param name="Disputed">A kind the model named against the sure one.</param>
/// <param name="Reason">Why the rules read the kind as they did (fact <c>video_kind_reason</c>); null for a protected file.</param>
public sealed record VideoOriginSummary(string Kind, string? Source, string? Certainty, string? Device = null, string? Title = null,
    int? Year = null, int? Season = null, int? Episode = null, string? Disputed = null, string? Reason = null)
{
    /// <summary>The summary of the stored facts of one video, or null when it has no video kind fact.</summary>
    public static VideoOriginSummary? From(IReadOnlyList<FileFact>? facts, bool withholdTitle = false)
    {
        var kind = facts?.FirstOrDefault(f => f.Key == FileFactKey.VideoKind);
        if (kind is null) return null;
        string? Value(FileFactKey key) => facts!.FirstOrDefault(f => f.Key == key)?.Value;
        int? Number(FileFactKey key) => int.TryParse(Value(key), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
        return new VideoOriginSummary(kind.Value, kind.Source.ToCode(), kind.Certainty?.ToCode(),
            Value(FileFactKey.RecordingDevice), withholdTitle ? null : Value(FileFactKey.VideoTitle),
            Number(FileFactKey.ReleaseYear), Number(FileFactKey.SeasonNumber), Number(FileFactKey.EpisodeNumber),
            Value(FileFactKey.VideoKindDisputed), withholdTitle ? null : Value(FileFactKey.VideoKindReason));
    }

    /// <summary>One line: <c>series_episode S02E05 "Example Show" (name, certain)</c>.</summary>
    public string ToText()
    {
        var parts = new List<string> { Kind };
        if (Device is not null) parts.Add(Device);
        if (Season is not null || Episode is not null) parts.Add($"S{Season ?? 0:00}E{Episode ?? 0:00}");
        if (Title is not null) parts.Add($"\"{Title}\"");
        if (Year is not null) parts.Add(Year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var text = string.Join(" ", parts);
        if (Source is not null) text += $" ({Source}{(Certainty is null ? string.Empty : ", " + Certainty)})";
        if (Disputed is not null) text += $", model said {Disputed}";
        return text;
    }
}
