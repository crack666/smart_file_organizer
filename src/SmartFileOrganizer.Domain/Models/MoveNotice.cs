using System.Globalization;
using System.Text;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>One file that left a folder: when, by which plan and action, its name there, and its new place.</summary>
public sealed record MoveNoticeEntry(DateTime MovedAt, long PlanId, long ActionId, string Name, string Target);

/// <summary>A read notice: its format version and its entries. Entries are only known for a version this build reads.</summary>
public sealed record MoveNoticeDocument(int Version, IReadOnlyList<MoveNoticeEntry> Entries)
{
    public bool Readable => Version == MoveNotice.Version;
}

/// <summary>
/// The move notice (https://github.com/crack666/smart_file_organizer/issues/86): a small plain text file that
/// <c>sfo plan apply --remove-sources</c> leaves in a folder whose files it moved, so the folder says where its content
/// went even without sfo and without the database. Format in docs/move-notice.md:
/// <code>
/// # sfo-move-notice 1
/// # (lines starting with # explain the file to a person)
/// moved&#9;2026-11-14T10:00:00Z&#9;5&#9;12&#9;IMG_0001.jpg&#9;/library/Fotos/IMG_0001.jpg
/// </code>
/// One line per file, fields separated by a tab: the word <c>moved</c>, date and time (UTC), plan, action, the file
/// name in this folder, its new place. A backslash, tab, line feed or carriage return in a field is written as
/// <c>\\</c>, <c>\t</c>, <c>\n</c>, <c>\r</c>. Nothing about the content of a file is written.
/// </summary>
public static class MoveNotice
{
    /// <summary>The fixed name of the notice in a folder.</summary>
    public const string FileName = "_moved-files.txt";

    /// <summary>The format version this build writes and reads.</summary>
    public const int Version = 1;

    /// <summary>Start of the first line; the version follows after a space.</summary>
    public const string Marker = "# sfo-move-notice";

    /// <summary>A notice larger than this is not a notice this tool wrote; it is left alone.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    private const string EntryWord = "moved";

    /// <summary>Whether <paramref name="path"/> carries the fixed name (the content still has to be checked).</summary>
    public static bool HasNoticeName(string path) =>
        string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a notice. Null when the text is not a notice (another file that happens to carry the name). A notice of
    /// another version is returned without entries (<see cref="MoveNoticeDocument.Readable"/> false).
    /// </summary>
    public static MoveNoticeDocument? Parse(string text)
    {
        var lines = text.TrimStart('﻿').Split('\n');
        var first = lines[0].TrimEnd('\r');
        if (!first.StartsWith(Marker + " ", StringComparison.Ordinal)
            || !int.TryParse(first[(Marker.Length + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            return null;
        if (version != Version)
            return new MoveNoticeDocument(version, []);

        var entries = new List<MoveNoticeEntry>();
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var fields = line.Split('\t');
            if (fields.Length != 6 || fields[0] != EntryWord) continue;
            if (!DateTime.TryParse(fields[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
                || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var plan)
                || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var action))
                continue;
            entries.Add(new MoveNoticeEntry(DateTime.SpecifyKind(at, DateTimeKind.Utc), plan, action, Unescape(fields[4]), Unescape(fields[5])));
        }
        return new MoveNoticeDocument(version, entries);
    }

    /// <summary>The text of a notice with these entries, oldest first. Lines end in a line feed.</summary>
    public static string Render(IEnumerable<MoveNoticeEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append(Marker).Append(' ').Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("# Files from this folder were moved by Smart File Organizer (sfo). The folder may stay empty.\n");
        sb.Append("# One line per file, separated by tabs: moved, date and time (UTC), plan, action, name here, new place.\n");
        sb.Append("# 'sfo where <old path>' shows the history; 'sfo plan undo <plan>' brings the files back.\n");
        foreach (var e in Order(entries))
        {
            sb.Append(EntryWord).Append('\t')
              .Append(e.MovedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\t')
              .Append(e.PlanId.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(e.ActionId.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(Escape(e.Name)).Append('\t')
              .Append(Escape(e.Target)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The order entries are written in: by time, plan, action and name.</summary>
    public static IEnumerable<MoveNoticeEntry> Order(IEnumerable<MoveNoticeEntry> entries) => entries
        .OrderBy(e => e.MovedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), StringComparer.Ordinal)
        .ThenBy(e => e.PlanId).ThenBy(e => e.ActionId).ThenBy(e => e.Name, StringComparer.Ordinal);

    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '\t' => "\\t",
                '\n' => "\\n",
                '\r' => "\\r",
                _ => c.ToString()
            });
        }
        return sb.ToString();
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            var next = value[++i];
            sb.Append(next switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => next });
        }
        return sb.ToString();
    }
}
