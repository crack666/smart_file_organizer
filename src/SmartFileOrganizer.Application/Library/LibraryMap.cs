using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SmartFileOrganizer.Application.Bundles;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Library;

/// <summary>
/// Kinds of content a target is chosen by. Internal to the matching: output shows file type codes, not these.
/// </summary>
public enum ContentGroup
{
    Media,
    Document,
    Audio,
    Archive,
    Software,
    Other
}

/// <summary>A period a folder name states: a day, a month or a year (or a range of years).</summary>
public sealed record NamePeriod(DateTime From, DateTime To, bool DayOrMonth);

/// <summary>How the subfolders of a folder are named: the habit of the library at that place.</summary>
/// <param name="Template">The pattern as a template, for example <c>{occasion} {year}</c>.</param>
/// <param name="NamePattern">The <see cref="Library.NamePattern"/> code behind it.</param>
/// <param name="Count">Subfolders that follow it.</param>
/// <param name="Of">Named subfolders looked at.</param>
public sealed record ChildrenPattern(string Template, string NamePattern, int Count, int Of, IReadOnlyList<string> Examples);

/// <summary>
/// One folder of the target map: what it holds, over which period, what it is for and how its subfolders are named.
/// Built from scan data only; a library job has no metadata facts and no model analysis.
/// </summary>
public sealed class LibraryMapEntry
{
    public required LibraryNode Node { get; init; }
    public LibraryMapEntry? Parent { get; internal set; }
    public List<LibraryMapEntry> Children { get; } = [];

    public string RelativePath => Node.RelativePath;
    public string Name => Node.Name;
    public string FullPath => Node.FullPath;
    public int Depth => Node.Depth;

    public FolderRole Role { get; internal set; }
    public FolderRole? ChildrenRole { get; internal set; }
    public string Purpose { get; internal set; } = string.Empty;
    public AdviceOrigin PurposeSource { get; internal set; } = AdviceOrigin.Rule;

    /// <summary>Content files in the subtree, without dumps below (a dump's own entry counts its whole tree).</summary>
    public long Files { get; internal set; }
    public long Bytes { get; internal set; }
    public long DirectFiles => Node.Files.Count;
    public int Subdirs => Node.SubdirCount;

    /// <summary>Files per <see cref="FileType"/> (index = enum value), as <see cref="Files"/>.</summary>
    public long[] Types { get; internal set; } = [];

    /// <summary>Earliest and latest time of the files: a date in the file name, else the modification time (local).</summary>
    public DateTime? From { get; internal set; }
    public DateTime? To { get; internal set; }
    public TimeSource? TimeSource { get; internal set; }

    /// <summary>The period the folder's name states, if any.</summary>
    public NamePeriod? NamePeriod { get; internal set; }

    public ChildrenPattern? ChildrenPattern { get; internal set; }

    /// <summary>The naming pattern of the patterns file for this folder, when the file names it (possibly edited by hand).</summary>
    public string? FilePattern { get; internal set; }

    /// <summary>Findings of the structure review at this folder (sensitive locations left out).</summary>
    public List<LibraryFinding> Findings { get; } = [];

    public DumpKind? Dump => Node.Dump == Node ? Node.DumpKind : null;

    /// <summary>Pattern for new subfolders: the patterns file wins over what the names show.</summary>
    public string? Pattern => FilePattern ?? ChildrenPattern?.Template;

    public double Share(ContentGroup group) => Files == 0 ? 0 : (double)LibraryMap.Count(Types, group) / Files;

    public ContentGroup? Dominant => Files == 0 ? null : LibraryMap.DominantGroup(Types);

    public override string ToString() => RelativePath;
}

/// <summary>
/// The target map of a library (https://github.com/crack666/smart_file_organizer/issues/25): every folder outside
/// dumps, each dump as one entry, with role, purpose, period, children pattern and the review findings there.
/// </summary>
public sealed partial class LibraryMap
{
    public required LibraryTree Tree { get; init; }
    public required LibraryRuleResult Rules { get; init; }
    public required LibraryMapEntry Root { get; init; }

    /// <summary>Entries in tree order: each folder before its subfolders, siblings by name.</summary>
    public required IReadOnlyList<LibraryMapEntry> Entries { get; init; }

    public required IReadOnlyDictionary<string, LibraryMapEntry> ByRelativePath { get; init; }
    public required IReadOnlyDictionary<string, LibraryMapEntry> ByFullPath { get; init; }

    /// <summary>Finding kinds that say nothing about how well a place is ordered.</summary>
    private static readonly LibraryFindingKind[] NotAboutOrder = [LibraryFindingKind.SensitiveLocation, LibraryFindingKind.DominantFile];

    /// <summary>
    /// Builds the map. <paramref name="tree"/> must have been through <see cref="LibraryRules.Run"/>, which marks dumps
    /// and computes the sums without them; <paramref name="rules"/> is its result.
    /// </summary>
    public static LibraryMap Build(LibraryTree tree, LibraryRuleResult rules, LibraryWords words, BundlePatterns patterns, LibraryMatchOptions options)
    {
        var typeCount = Enum.GetValues<FileType>().Length;

        // Times per directory: direct files first, then the subtree, with and without dumps.
        var live = new Dictionary<LibraryNode, TimeSpan3>();
        var whole = new Dictionary<LibraryNode, TimeSpan3>();
        for (var i = tree.All.Count - 1; i >= 0; i--)
        {
            var n = tree.All[i];
            var own = new TimeSpan3();
            foreach (var f in n.Files)
                own.Add(TimeOf(f.Name, f.LastWriteTime));
            var w = own.Copy();
            var l = n.InDump ? new TimeSpan3() : own.Copy();
            foreach (var c in n.Children)
            {
                w.Merge(whole[c]);
                if (!c.InDump) l.Merge(live[c]);
            }
            whole[n] = w;
            live[n] = l;
        }

        // Entries in tree order.
        var entries = new List<LibraryMapEntry>();
        LibraryMapEntry Visit(LibraryNode node, LibraryMapEntry? parent)
        {
            var isDump = node.Dump == node;
            var span = isDump ? whole[node] : live[node];
            var entry = new LibraryMapEntry
            {
                Node = node,
                Parent = parent,
                Files = isDump ? node.TreeFiles : node.LiveFiles,
                Bytes = isDump ? node.TreeBytes : node.LiveBytes,
                Types = (isDump ? node.TreeTypes : node.LiveTypes).ToArray(),
                From = span.From,
                To = span.To,
                TimeSource = span.Source,
                NamePeriod = ParseNamePeriod(node.Name)
            };
            if (entry.Types.Length != typeCount) entry.Types = new long[typeCount];
            entries.Add(entry);
            parent?.Children.Add(entry);
            if (!isDump)
            {
                foreach (var child in node.Children.Where(c => !c.InDump || c.Dump == c))
                    Visit(child, entry);
            }
            return entry;
        }
        var root = Visit(tree.Root, null);

        var byRelative = entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var byFull = entries.ToDictionary(e => e.FullPath, StringComparer.Ordinal);

        foreach (var finding in rules.Findings)
        {
            if (NotAboutOrder.Contains(finding.Kind)) continue;
            var places = new HashSet<string>(StringComparer.Ordinal) { finding.RelativePath };
            foreach (var item in finding.Items)
                places.Add(item);
            foreach (var place in places)
            {
                if (byRelative.TryGetValue(place, out var e) && !e.Findings.Contains(finding))
                    e.Findings.Add(finding);
            }
        }

        // Roles bottom-up: a collection is judged by the roles of its children.
        for (var i = entries.Count - 1; i >= 0; i--)
            AssignRole(entries[i], words, patterns, options);
        foreach (var e in entries)
        {
            e.ChildrenPattern = FindChildrenPattern(e, words);
            e.Purpose = RulePurpose(e);
        }

        return new LibraryMap
        {
            Tree = tree,
            Rules = rules,
            Root = root,
            Entries = entries,
            ByRelativePath = byRelative,
            ByFullPath = byFull
        };
    }

    /// <summary>
    /// The patterns of the library as a target file (<c>sfo_target_structure</c>): one target per folder whose
    /// subfolders share a role and a naming pattern. Null when there is none.
    /// </summary>
    public TargetStructure? ToPatterns(ScanJob job)
    {
        var targets = new List<TargetEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Entries)
        {
            if (e.Depth == 0 || e.Role != FolderRole.Collection || e.ChildrenRole is null || e.ChildrenPattern is null) continue;
            var id = Slug(e.RelativePath);
            var unique = id;
            for (var n = 2; !ids.Add(unique); n++) unique = $"{id}_{n}";
            var holds = Enumerable.Range(0, e.Types.Length)
                .Where(t => (FileType)t is not (FileType.Unknown or FileType.Other) && e.Files > 0 && e.Types[t] * 10 >= e.Files)
                .OrderByDescending(t => e.Types[t])
                .Select(t => ((FileType)t).ToString().ToLowerInvariant())
                .ToList();
            targets.Add(new TargetEntry(unique, e.RelativePath, e.ChildrenPattern.Template, holds, e.Purpose, e.ChildrenPattern.Examples));
        }
        return targets.Count == 0
            ? null
            : new TargetStructure(TargetStructureFile.SchemaVersion, TargetStructureFile.KindValue, job.RootPath, job.Id,
                DateTime.UtcNow, AdviceOrigin.Rule, null, targets);
    }

    /// <summary>
    /// Takes the patterns of a (possibly hand-edited) patterns file over. Returns the targets whose path is not a
    /// folder of the map; they are ignored.
    /// </summary>
    public IReadOnlyList<string> Apply(TargetStructure patterns)
    {
        foreach (var e in Entries)
            e.FilePattern = null;
        var missing = new List<string>();
        foreach (var t in patterns.Targets)
        {
            if (ByRelativePath.TryGetValue(t.Path.Replace('\\', '/').Trim('/'), out var e))
                e.FilePattern = string.IsNullOrWhiteSpace(t.Pattern) ? null : t.Pattern.Trim();
            else
                missing.Add(t.Path);
        }
        return missing;
    }

    // ------------------------------------------------------------------ roles and purposes

    private static void AssignRole(LibraryMapEntry e, LibraryWords words, BundlePatterns patterns, LibraryMatchOptions o)
    {
        e.ChildrenRole = null;
        if (e.Dump is not null)
        {
            e.Role = FolderRole.Dump;
            return;
        }
        if (e.Depth > 0 && LibraryRules.PlaceholderKind(e.Name, words) is not null)
        {
            e.Role = FolderRole.CatchAll;
            return;
        }
        if (e.Depth > 0 && IsTimeName(e.Name))
        {
            e.Role = FolderRole.Time;
            return;
        }

        // A folder of named folders with content is a collection, judged by what its children are.
        var named = e.Children.Where(c => c.Files > 0 && !patterns.IsGenericName(c.Name)).ToList();
        if (named.Count >= 2 && e.DirectFiles * 2 <= e.Files)
        {
            e.Role = FolderRole.Collection;
            var roles = named.Where(c => c.Role != FolderRole.CatchAll)
                .GroupBy(c => c.Role)
                .Select(g => (Role: g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count).ThenBy(g => g.Role)
                .ToList();
            if (roles.Count > 0 && (double)roles[0].Count / named.Count >= o.RoleShare)
                e.ChildrenRole = roles[0].Role;
            return;
        }

        if (e.Files == 0)
        {
            e.Role = FolderRole.Topic;
            return;
        }
        if (e.Share(ContentGroup.Media) >= o.RoleShare)
            e.Role = e.Depth > 0 && !patterns.IsGenericName(e.Name) ? FolderRole.Occasion : FolderRole.Topic;
        else if (e.Share(ContentGroup.Document) >= o.RoleShare)
            e.Role = FolderRole.DocumentType;
        else if (e.Share(ContentGroup.Audio) >= o.RoleShare)
            e.Role = FolderRole.MediaTitle;
        else if (e.Share(ContentGroup.Software) >= 0.5 || e.Node.Profile.UnitMarkers.Count > 0)
            e.Role = FolderRole.Project;
        else if (e.Types.Count(t => t > 0) >= 3 && e.Types.Max() * 2 < e.Files)
            e.Role = FolderRole.Mixed;
        else
            e.Role = FolderRole.Topic;
    }

    private static string RulePurpose(LibraryMapEntry e)
    {
        var counts = Counts(e.Types);
        var period = PeriodText(e);
        return e.Role switch
        {
            FolderRole.Dump => $"Copy of a {e.Dump.ToCode()} ({e.Node.DumpReason}); kept whole, not a place to file into.",
            FolderRole.CatchAll => $"Catch-all folder: {Or(counts, "empty")}; not a good place to file into.",
            FolderRole.Time => $"Everything from {e.Name}: {Or(counts, "empty")}.",
            FolderRole.Collection when e.ChildrenRole is { } r && e.ChildrenPattern is { } p =>
                $"{e.Children.Count} folders, mostly {RoleWords(r)} named like '{p.Template}' ({string.Join(", ", p.Examples)}): {counts}{period}.",
            FolderRole.Collection when e.ChildrenRole is { } r =>
                $"{e.Children.Count} folders, mostly {RoleWords(r)}: {counts}{period}.",
            FolderRole.Collection => $"{e.Children.Count} folders of different kinds: {counts}{period}.",
            FolderRole.Occasion => $"Photos and videos of one occasion: {counts}{period}.",
            FolderRole.DocumentType => $"Documents of one kind: {counts}{period}.",
            FolderRole.MediaTitle => $"Music or recordings: {counts}{period}.",
            FolderRole.Project => $"A program or project, kept whole: {counts}.",
            FolderRole.Mixed => $"Mixed content: {counts}{period}.",
            _ => e.Files == 0 ? "Empty folder." : $"Folder of {counts}{period}."
        };
    }

    private static string Or(string text, string fallback) => text.Length == 0 ? fallback : text;

    public static string RoleWords(FolderRole role) => role switch
    {
        FolderRole.Occasion => "occasions",
        FolderRole.Person => "persons",
        FolderRole.Device => "devices",
        FolderRole.Topic => "topics",
        FolderRole.Time => "years",
        FolderRole.MediaTitle => "albums or titles",
        FolderRole.Season => "seasons",
        FolderRole.DocumentType => "kinds of documents",
        FolderRole.Project => "projects",
        FolderRole.Collection => "collections",
        FolderRole.CatchAll => "catch-all folders",
        FolderRole.Dump => "device copies",
        _ => "mixed folders"
    };

    /// <summary>"120 image, 3 video" for the three largest types.</summary>
    public static string Counts(long[] types) => string.Join(", ",
        Enumerable.Range(0, types.Length)
            .Where(t => types[t] > 0)
            .OrderByDescending(t => types[t]).ThenBy(t => t)
            .Take(3)
            .Select(t => $"{types[t]} {((FileType)t).ToString().ToLowerInvariant()}"));

    private static string PeriodText(LibraryMapEntry e)
    {
        if (e.From is not { } from || e.To is not { } to) return string.Empty;
        return e.TimeSource == Domain.Enums.TimeSource.FileName
            ? $", taken {Day(from)} to {Day(to)}"
            : from.Year == to.Year ? $", files from {from.Year}" : $", files from {from.Year} to {to.Year}";
    }

    public static string Day(DateTime t) => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ children pattern

    private static ChildrenPattern? FindChildrenPattern(LibraryMapEntry e, LibraryWords words)
    {
        var named = e.Children
            .Where(c => c.Dump is null && LibraryRules.PlaceholderKind(c.Name, words) is null)
            .Select(c => (c.Name, Pattern: LibraryText.Classify(c.Name)))
            .Where(c => c.Pattern is not (NamePattern.Other or NamePattern.Camera))
            .ToList();
        if (named.Count < 2) return null;

        var groups = named.GroupBy(c => c.Pattern)
            .Select(g => (Pattern: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Pattern, StringComparer.Ordinal)
            .ToList();
        // The habit for occasions is the most common pattern with a date, as the naming rule says.
        var dated = groups.FirstOrDefault(g => NamePattern.IsDated(g.Pattern));
        var chosen = dated.Count >= 2 ? dated : groups[0];
        if (chosen.Count < 2) return null;

        var template = Template(chosen.Pattern, e.ChildrenRole);
        if (template is null) return null;
        var examples = named.Where(c => c.Pattern == chosen.Pattern).Select(c => c.Name).Take(3).ToList();
        return new ChildrenPattern(template, chosen.Pattern, chosen.Count, named.Count, examples);
    }

    /// <summary>The template of a name pattern: <c>year_suffix</c> of occasions is <c>{occasion} {year}</c>.</summary>
    public static string? Template(string pattern, FolderRole? childrenRole)
    {
        var word = childrenRole == FolderRole.Occasion ? "{occasion}" : "{name}";
        return pattern switch
        {
            NamePattern.YearSuffix or NamePattern.YearInside => $"{word} {{year}}",
            NamePattern.YearPrefix => $"{{year}} {word}",
            NamePattern.DateYmdPrefix => $"{{date}} {word}",
            NamePattern.DateCompactPrefix => $"{{date_compact}} {word}",
            NamePattern.DateDmyPrefix => $"{{date_dmy}} {word}",
            NamePattern.YearMonthPrefix => $"{{month}} {word}",
            NamePattern.DateSuffix => $"{word} {{date}}",
            NamePattern.YearOnly => "{year}",
            NamePattern.NoDate => word,
            _ => null
        };
    }

    // ------------------------------------------------------------------ times and names

    /// <summary>The time of a file of a library: a date in its name, else its modification time in local time.</summary>
    public static (DateTime? Time, TimeSource Source) TimeOf(string name, DateTime lastWriteUtc)
    {
        if (FileNameDates.TryParse(name) is { } named)
            return (named, Domain.Enums.TimeSource.FileName);
        if (lastWriteUtc <= DateTime.MinValue.AddDays(1))
            return (null, Domain.Enums.TimeSource.ModifiedDate);
        var local = DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc).ToLocalTime();
        return (DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Domain.Enums.TimeSource.ModifiedDate);
    }

    /// <summary>A name that is only a year or a year and month: <c>2015</c>, <c>2015-07</c>.</summary>
    public static bool IsTimeName(string name) => TimeNameRegex().IsMatch(name.Trim());

    /// <summary>The period a folder name states: a date, a month, a year or a range of years; null for none.</summary>
    public static NamePeriod? ParseNamePeriod(string name)
    {
        var n = name.Trim();
        if (YmdRegex().Match(n) is { Success: true } ymd && TryDate(ymd.Groups["y"].Value, ymd.Groups["m"].Value, ymd.Groups["d"].Value) is { } day)
            return new NamePeriod(day, day.AddDays(1).AddTicks(-1), true);
        if (DmyRegex().Match(n) is { Success: true } dmy && TryDate(dmy.Groups["y"].Value, dmy.Groups["m"].Value, dmy.Groups["d"].Value) is { } day2)
            return new NamePeriod(day2, day2.AddDays(1).AddTicks(-1), true);
        if (YmRegex().Match(n) is { Success: true } ym && TryDate(ym.Groups["y"].Value, ym.Groups["m"].Value, "01") is { } month)
            return new NamePeriod(month, month.AddMonths(1).AddTicks(-1), true);
        var years = LibraryText.Years(n).Select(y => int.Parse(y, CultureInfo.InvariantCulture)).Distinct().Order().ToList();
        if (years.Count == 0 || years[^1] - years[0] > 5) return null;
        return new NamePeriod(new DateTime(years[0], 1, 1), new DateTime(years[^1], 12, 31, 23, 59, 59), false);
    }

    private static DateTime? TryDate(string y, string m, string d) =>
        DateTime.TryParseExact($"{y}-{m.PadLeft(2, '0')}-{d.PadLeft(2, '0')}", "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t)
            ? t
            : null;

    /// <summary>Words of a name for comparing: letters only, at least 3, lower case with umlauts transliterated, stop words out.</summary>
    public static IReadOnlyList<string> Words(string name, LibraryWords words) =>
        LibraryText.Tokens(LibraryWords.Normalize(name))
            .Where(t => t.Length >= 3 && t.All(char.IsLetter) && !words.StopWords.Contains(t) && !CameraWords.Contains(t))
            .Distinct()
            .ToList();

    /// <summary>Prefixes cameras and phones write; they say nothing about the content.</summary>
    private static readonly HashSet<string> CameraWords = new(StringComparer.Ordinal)
    {
        "img", "dsc", "dscn", "dscf", "mvi", "mov", "vid", "pxl", "gopr", "screenshot", "bildschirmfoto", "image", "video",
        "photo", "foto", "whatsapp", "jpg", "jpeg", "png", "heic", "mp4", "scan"
    };

    /// <summary>Two words mean the same: equal, one a prefix of the other (at least 4 letters), or one letter apart.</summary>
    public static bool SameWord(string a, string b)
    {
        if (a == b) return true;
        if (Math.Min(a.Length, b.Length) >= 4 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)))
            return true;
        return Math.Min(a.Length, b.Length) >= 5 && LibraryText.Distance(a, b, 1) <= 1;
    }

    // ------------------------------------------------------------------ type groups

    public static ContentGroup GroupOf(FileType type) => type switch
    {
        FileType.Image or FileType.Video => ContentGroup.Media,
        FileType.Document => ContentGroup.Document,
        FileType.Audio => ContentGroup.Audio,
        FileType.Archive => ContentGroup.Archive,
        FileType.Code or FileType.Executable or FileType.Database => ContentGroup.Software,
        _ => ContentGroup.Other
    };

    public static long Count(long[] types, ContentGroup group)
    {
        long sum = 0;
        for (var t = 0; t < types.Length; t++)
        {
            if (GroupOf((FileType)t) == group) sum += types[t];
        }
        return sum;
    }

    public static ContentGroup? DominantGroup(long[] types)
    {
        ContentGroup? best = null;
        long bestCount = 0;
        foreach (var g in Enum.GetValues<ContentGroup>())
        {
            var c = Count(types, g);
            if (c > bestCount)
            {
                best = g;
                bestCount = c;
            }
        }
        return best;
    }

    public static string GroupWords(ContentGroup group) => group switch
    {
        ContentGroup.Media => "photos and videos",
        ContentGroup.Document => "documents",
        ContentGroup.Audio => "audio",
        ContentGroup.Archive => "archives",
        ContentGroup.Software => "programs and code",
        _ => "other files"
    };

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in LibraryWords.Normalize(text))
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? c : '_');
        var slug = MultiUnderscoreRegex().Replace(sb.ToString(), "_").Trim('_');
        return slug.Length == 0 ? "target" : slug;
    }

    /// <summary>Earliest and latest time of a set of files and what the times rest on.</summary>
    private sealed class TimeSpan3
    {
        public DateTime? From { get; private set; }
        public DateTime? To { get; private set; }
        private bool _named;
        private bool _modified;

        public TimeSource? Source => _named && _modified ? Domain.Enums.TimeSource.Mixed
            : _named ? Domain.Enums.TimeSource.FileName
            : _modified ? Domain.Enums.TimeSource.ModifiedDate
            : null;

        public void Add((DateTime? Time, TimeSource Source) t)
        {
            if (t.Time is not { } time) return;
            if (From is null || time < From) From = time;
            if (To is null || time > To) To = time;
            if (t.Source == Domain.Enums.TimeSource.FileName) _named = true;
            else _modified = true;
        }

        public void Merge(TimeSpan3 other)
        {
            if (other.From is { } f && (From is null || f < From)) From = f;
            if (other.To is { } t && (To is null || t > To)) To = t;
            _named |= other._named;
            _modified |= other._modified;
        }

        public TimeSpan3 Copy()
        {
            var c = new TimeSpan3();
            c.Merge(this);
            return c;
        }
    }

    [GeneratedRegex(@"^\(?(?:19|20)\d{2}(?:[-_. ](?:0[1-9]|1[0-2]))?\)?$")]
    private static partial Regex TimeNameRegex();

    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})[-_.]?(?<m>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])(?!\d)")]
    private static partial Regex YmdRegex();

    [GeneratedRegex(@"(?<!\d)(?<d>0?[1-9]|[12]\d|3[01])\.(?<m>0?[1-9]|1[0-2])\.(?<y>(?:19|20)\d{2})(?!\d)")]
    private static partial Regex DmyRegex();

    [GeneratedRegex(@"(?<!\d)(?<y>(?:19|20)\d{2})[-_.](?<m>0[1-9]|1[0-2])(?![\d])")]
    private static partial Regex YmRegex();

    [GeneratedRegex("_+")]
    private static partial Regex MultiUnderscoreRegex();
}
