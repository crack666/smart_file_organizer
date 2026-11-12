using System.Globalization;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Bundles;

/// <summary>
/// The time of one file and what it rests on, with the device and the image origin
/// (<c>camera</c>, <c>screenshot</c>, <c>scan</c>; null when unknown) that keep different streams apart.
/// </summary>
public readonly record struct FileTime(DateTime? Time, TimeSource Source, string? Device, ImageOrigin? Origin = null);

/// <summary>
/// Groups photos and videos into occasions by the pauses between them, without a model
/// (https://github.com/crack666/smart_file_organizer/issues/39).
/// </summary>
/// <remarks>
/// Built to rather make two bundles too many than to put two occasions into one:
/// <list type="bullet">
/// <item>Files of different devices (when the metadata name one) are never grouped together, and neither are
/// screenshots or scans with camera pictures.</item>
/// <item>Files whose only time is the modification time are grouped per directory: a copy gives every
/// file the time of the copy, so across directories such times say nothing about the occasion.</item>
/// <item>A group longer than <see cref="BundleOptions.MaxEventDays"/> is cut at its largest pauses.</item>
/// </list>
/// </remarks>
public static class EventGrouper
{
    /// <summary>The time of a file: capture date from the metadata, else a date in the name, else the modification time.</summary>
    public static FileTime TimeOf(FileNode file, IReadOnlyDictionary<long, CaptureInfo> captures)
    {
        captures.TryGetValue(file.Id, out var capture);
        var device = string.IsNullOrWhiteSpace(capture?.Device) ? null : capture.Device.Trim();
        var origin = capture?.ImageOrigin ?? (LooksLikeScreenshot(file.Name) ? ImageOrigin.Screenshot : null);

        if (capture?.TakenAt is { } taken)
            return new FileTime(taken, TimeSource.CaptureDate, device, origin);
        if (FileNameDates.TryParse(file.Name) is { } named)
            return new FileTime(named, TimeSource.FileName, device, origin);
        if (file.LastWriteTime <= DateTime.MinValue.AddDays(1))
            return new FileTime(null, TimeSource.ModifiedDate, device, origin);
        // Stored as UTC; capture dates and names carry the local wall-clock time, so compare in local time.
        var local = DateTime.SpecifyKind(file.LastWriteTime, DateTimeKind.Utc).ToLocalTime();
        return new FileTime(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSource.ModifiedDate, device, origin);
    }

    /// <summary>Screenshot names as phones and desktops write them, for files analysed before metadata facts existed.</summary>
    private static bool LooksLikeScreenshot(string name) =>
        name.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Bildschirmfoto", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Screen Shot", StringComparison.OrdinalIgnoreCase);

    /// <summary>Screenshots and scans are never grouped with camera pictures; camera and unknown origin are.</summary>
    private static string OriginKey(ImageOrigin? origin) => origin switch
    {
        ImageOrigin.Screenshot => "screenshot",
        ImageOrigin.Scan => "scan",
        _ => string.Empty
    };

    /// <summary>
    /// Events and the scattered rest of <paramref name="files"/>, ordered by time. Each file ends in exactly one
    /// returned bundle (<see cref="Bundle.FileIds"/>); counts, times and facts are left to the caller.
    /// </summary>
    public static List<Bundle> Group(
        IReadOnlyList<FileNode> files,
        Func<FileNode, FileTime> timeOf,
        BundleOptions options)
    {
        var gap = TimeSpan.FromHours(options.EventGapHours);
        var maxSpan = TimeSpan.FromDays(options.MaxEventDays);

        var timed = files.Select(f => (File: f, Time: timeOf(f))).ToList();
        var groups = new List<List<(FileNode File, FileTime Time)>>();
        var rest = new List<(FileNode File, FileTime Time)>();

        rest.AddRange(timed.Where(t => t.Time.Time is null));

        var partitions = timed
            .Where(t => t.Time.Time is not null)
            .GroupBy(t => (Origin: OriginKey(t.Time.Origin),
                           Device: t.Time.Device ?? string.Empty,
                           Directory: t.Time.Source == TimeSource.ModifiedDate ? t.File.ParentPath : string.Empty));

        foreach (var partition in partitions.OrderBy(p => p.Key.Origin, StringComparer.Ordinal)
                                            .ThenBy(p => p.Key.Device, StringComparer.Ordinal)
                                            .ThenBy(p => p.Key.Directory, StringComparer.Ordinal))
        {
            var sorted = partition.OrderBy(t => t.Time.Time).ThenBy(t => t.File.FullPath, StringComparer.Ordinal).ToList();
            var current = new List<(FileNode, FileTime)> { sorted[0] };
            for (var i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Time.Time!.Value - sorted[i - 1].Time.Time!.Value > gap)
                {
                    groups.AddRange(SplitLong(current, maxSpan));
                    current = [];
                }
                current.Add(sorted[i]);
            }
            groups.AddRange(SplitLong(current, maxSpan));
        }

        var bundles = new List<Bundle>();
        foreach (var group in groups)
        {
            if (group.Count < options.MinEventFiles)
            {
                rest.AddRange(group);
                continue;
            }
            bundles.Add(new Bundle
            {
                Kind = BundleKind.Event,
                FileIds = group.Select(g => g.File.Id).ToList()
            });
        }

        // The rest: one bundle per year, plus one for files without any time.
        foreach (var byYear in rest.GroupBy(r => r.Time.Time?.Year).OrderBy(g => g.Key ?? int.MaxValue))
        {
            bundles.Add(new Bundle
            {
                Kind = BundleKind.Scattered,
                SuggestedName = byYear.Key is { } year
                    ? $"{year.ToString(CultureInfo.InvariantCulture)} (scattered)"
                    : "Without date (scattered)",
                FileIds = byYear.Select(r => r.File.Id).ToList()
            });
        }

        return bundles;
    }

    /// <summary>Cuts a group at its largest pause until no part spans more than <paramref name="maxSpan"/>.</summary>
    private static IEnumerable<List<(FileNode File, FileTime Time)>> SplitLong(List<(FileNode File, FileTime Time)> group, TimeSpan maxSpan)
    {
        if (group.Count < 2 || group[^1].Time.Time!.Value - group[0].Time.Time!.Value <= maxSpan)
        {
            yield return group;
            yield break;
        }

        var cut = 1;
        var largest = TimeSpan.MinValue;
        for (var i = 1; i < group.Count; i++)
        {
            var pause = group[i].Time.Time!.Value - group[i - 1].Time.Time!.Value;
            if (pause > largest)
            {
                largest = pause;
                cut = i;
            }
        }

        foreach (var part in SplitLong(group[..cut], maxSpan))
            yield return part;
        foreach (var part in SplitLong(group[cut..], maxSpan))
            yield return part;
    }
}
