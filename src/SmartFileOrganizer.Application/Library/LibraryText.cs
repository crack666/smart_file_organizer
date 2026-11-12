using System.Text;
using System.Text.RegularExpressions;

namespace SmartFileOrganizer.Application.Library;

/// <summary>
/// How a folder name carries a date. The codes appear in findings (<c>naming_inconsistent</c>) and in the
/// facts sent to the model, so they are stable.
/// </summary>
public static class NamePattern
{
    public const string DateYmdPrefix = "date_ymd_prefix";        // 2015-05-17 Ski trip
    public const string DateCompactPrefix = "date_compact_prefix"; // 20150517 Ski trip
    public const string DateDmyPrefix = "date_dmy_prefix";        // 17.05.2015 Ski trip
    public const string YearMonthPrefix = "year_month_prefix";    // 2015-05 Ski trip
    public const string YearPrefix = "year_prefix";               // 2015 Ski trip
    public const string DateSuffix = "date_suffix";               // Ski trip 2015-05-17, Ski trip 17.05.2015
    public const string YearSuffix = "year_suffix";               // Ski trip 2015, Ski trip (2015)
    public const string YearInside = "year_inside";               // Ski trip 2015 part 2
    public const string YearOnly = "year_only";                   // 2015
    public const string NoDate = "no_date";                       // Ski trip
    public const string Camera = "camera";                        // 100CANON
    public const string Other = "other";                          // 0042, ---

    public static bool IsDated(string pattern) =>
        pattern is not (NoDate or Camera or Other);
}

/// <summary>Name helpers of the structure rules: tokens, comparison keys, date patterns. Pure functions.</summary>
public static partial class LibraryText
{
    /// <summary>A share as a whole percentage, the same on every machine: <c>0.8</c> → <c>80%</c>.</summary>
    public static string Percent(double share) =>
        Math.Round(share * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>Bytes with a binary unit, the same on every machine: <c>1.5 GB</c>.</summary>
    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>Words of a normalised name: runs of letters or digits.</summary>
    public static IReadOnlyList<string> Tokens(string normalized) =>
        TokenRegex().Matches(normalized).Select(m => m.Value).ToList();

    /// <summary>
    /// Key for "same name elsewhere": letters only, years and other digits removed. <c>Rechnungen 2015</c> and
    /// <c>rechnungen</c> share the key <c>rechnungen</c>.
    /// </summary>
    public static string NameKey(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in LibraryWords.Normalize(name))
        {
            if (char.IsLetter(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Key for "similar names": the set of words (letters only, stop words dropped), sorted. <c>Urlaub Italien</c>
    /// and <c>Italien Urlaub 2</c> share it.
    /// </summary>
    public static string WordSetKey(string name, IReadOnlySet<string> stopWords) =>
        string.Join(' ', Tokens(LibraryWords.Normalize(name))
            .Where(t => t.All(char.IsLetter) && !stopWords.Contains(t))
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal));

    /// <summary>The four-digit years (1900-2099) in a name.</summary>
    public static IReadOnlyList<string> Years(string name) =>
        YearRegex().Matches(name).Select(m => m.Value).ToList();

    public static bool IsCameraFolder(string name) => CameraFolderRegex().IsMatch(name);

    /// <summary>Classifies how <paramref name="name"/> carries a date; one of the <see cref="NamePattern"/> codes.</summary>
    public static string Classify(string name)
    {
        var n = name.Trim();
        if (IsCameraFolder(n)) return NamePattern.Camera;
        if (YearOnlyRegex().IsMatch(n)) return NamePattern.YearOnly;
        if (DateYmdPrefixRegex().IsMatch(n)) return NamePattern.DateYmdPrefix;
        if (DateCompactPrefixRegex().IsMatch(n)) return NamePattern.DateCompactPrefix;
        if (DateDmyPrefixRegex().IsMatch(n)) return NamePattern.DateDmyPrefix;
        if (YearMonthPrefixRegex().IsMatch(n)) return NamePattern.YearMonthPrefix;
        if (YearPrefixRegex().IsMatch(n)) return NamePattern.YearPrefix;
        if (DateSuffixRegex().IsMatch(n)) return NamePattern.DateSuffix;
        if (YearSuffixRegex().IsMatch(n)) return NamePattern.YearSuffix;
        if (YearRegex().IsMatch(n)) return NamePattern.YearInside;
        return n.Any(char.IsLetter) ? NamePattern.NoDate : NamePattern.Other;
    }

    /// <summary>Edit distance, stopped early above <paramref name="max"/> (returns <c>max + 1</c>).</summary>
    public static int Distance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > max) return max + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// A name typed on a few neighbouring keys, such as <c>asdf</c>, <c>jjj</c> or <c>qwe</c>: 3 to 4 letters,
    /// each the same as or next to the previous one on a QWERTZ or QWERTY row.
    /// </summary>
    public static bool IsKeyboardMash(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n.Length is < 3 or > 4 || !n.All(c => c is >= 'a' and <= 'z')) return false;
        for (var i = 1; i < n.Length; i++)
        {
            if (n[i] != n[i - 1] && !Neighbours(n[i - 1], n[i])) return false;
        }
        return true;
    }

    private static readonly string[] KeyRows = ["qwertzuiop", "qwertyuiop", "asdfghjkl", "yxcvbnm", "zxcvbnm"];

    private static bool Neighbours(char a, char b) =>
        KeyRows.Any(row => row.IndexOf(a) is var i and >= 0 && row.IndexOf(b) is var j and >= 0 && Math.Abs(i - j) == 1);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}(?!\d)")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"^\d{3}[A-Z_]{5}$")]
    private static partial Regex CameraFolderRegex();

    [GeneratedRegex(@"^\(?(?:19|20)\d{2}\)?$")]
    private static partial Regex YearOnlyRegex();

    [GeneratedRegex(@"^(?:19|20)\d{2}[-_.](?:0[1-9]|1[0-2])[-_.](?:0[1-9]|[12]\d|3[01])(?!\d)")]
    private static partial Regex DateYmdPrefixRegex();

    [GeneratedRegex(@"^(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])(?!\d)")]
    private static partial Regex DateCompactPrefixRegex();

    [GeneratedRegex(@"^(?:0?[1-9]|[12]\d|3[01])\.(?:0?[1-9]|1[0-2])\.(?:(?:19|20)\d{2}|\d{2})(?!\d)")]
    private static partial Regex DateDmyPrefixRegex();

    [GeneratedRegex(@"^(?:19|20)\d{2}[-_.](?:0[1-9]|1[0-2])(?!\d)")]
    private static partial Regex YearMonthPrefixRegex();

    [GeneratedRegex(@"^(?:19|20)\d{2}(?!\d)")]
    private static partial Regex YearPrefixRegex();

    [GeneratedRegex(@"(?:(?:19|20)\d{2}[-_.](?:0[1-9]|1[0-2])[-_.](?:0[1-9]|[12]\d|3[01])|(?:0?[1-9]|[12]\d|3[01])\.(?:0?[1-9]|1[0-2])\.(?:19|20)\d{2})\)?$")]
    private static partial Regex DateSuffixRegex();

    [GeneratedRegex(@"(?<!\d)\(?(?:19|20)\d{2}\)?$")]
    private static partial Regex YearSuffixRegex();
}
