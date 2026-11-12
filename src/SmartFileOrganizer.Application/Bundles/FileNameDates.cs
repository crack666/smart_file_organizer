using System.Text.RegularExpressions;

namespace SmartFileOrganizer.Application.Bundles;

/// <summary>
/// Reads a capture date from a file name as cameras, phones and messengers write it:
/// <c>IMG_20150712_143000.jpg</c>, <c>20150712_143000.mp4</c>, <c>PXL_20210304_101500123.jpg</c>,
/// <c>IMG-20150712-WA0001.jpg</c>, <c>Screenshot_2015-07-12-14-30-00.png</c>,
/// <c>WhatsApp Image 2015-07-12 at 14.30.00.jpeg</c>. A name with a date but no time gets noon,
/// so files of the same day stay together and different days stay apart.
/// </summary>
public static partial class FileNameDates
{
    [GeneratedRegex(
        @"(?<!\d)(?<y>(?:19|20)\d{2})[-_.]?(?<m>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])" +
        @"(?:(?:[ _T-]|\sat\s)(?<H>[01]\d|2[0-3])[-_.:h]?(?<M>[0-5]\d)(?:[-_.:m]?(?<S>[0-5]\d))?)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();

    /// <summary>The date in <paramref name="fileName"/>, or null. Years before 1990 or in the future are ignored.</summary>
    public static DateTime? TryParse(string fileName, DateTime? now = null)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var limit = (now ?? DateTime.Now).Year + 1;

        foreach (Match match in DatePattern().Matches(stem))
        {
            var year = int.Parse(match.Groups["y"].Value);
            if (year < 1990 || year > limit) continue;
            var month = int.Parse(match.Groups["m"].Value);
            var day = int.Parse(match.Groups["d"].Value);
            if (day > DateTime.DaysInMonth(year, month)) continue;

            if (!match.Groups["H"].Success)
                return new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Unspecified);

            var hour = int.Parse(match.Groups["H"].Value);
            var minute = int.Parse(match.Groups["M"].Value);
            var second = match.Groups["S"].Success ? int.Parse(match.Groups["S"].Value) : 0;
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }

        return null;
    }
}
