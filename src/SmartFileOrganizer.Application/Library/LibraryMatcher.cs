using System.Globalization;
using System.Text.RegularExpressions;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Library;

/// <summary>
/// What <c>sfo library match</c> looks for a place for: a file, a folder or a bundle of a source, described by
/// scan data, metadata facts and the comparison with the library. No content is read here.
/// </summary>
public sealed record MatchUnit
{
    public required MatchUnitKind Kind { get; init; }

    /// <summary>The file or folder (full path); for a bundle its root folder, null for an event.</summary>
    public string? FullPath { get; init; }

    public long? BundleId { get; init; }
    public BundleKind? BundleKind { get; init; }

    /// <summary>The name a person knows it by: folder name, file name, or the bundle's suggested name.</summary>
    public required string Name { get; init; }

    /// <summary>A named folder (or dump) that is placed as a whole under its own name: never renamed, never split.</summary>
    public bool KeepWhole { get; init; }

    public string KeepReason { get; init; } = string.Empty;

    /// <summary>What the unit is, in the roles of the library: an occasion, a kind of documents, a device copy, ...</summary>
    public FolderRole Role { get; init; }

    public long Files { get; init; }

    /// <summary>Files per <see cref="FileType"/> (index = enum value).</summary>
    public required long[] Types { get; init; }

    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public TimeSource? TimeSource { get; init; }

    /// <summary>Words to compare with folder names: the folder's name, or words common to the file names and meaningful folders.</summary>
    public IReadOnlyList<string> Words { get; init; } = [];

    /// <summary>For each file already in the library: the library folder (full path) that holds the same content.</summary>
    public IReadOnlyList<string> MatchDirectories { get; init; } = [];

    /// <summary>Files per comparison state; null when the source was not compared with this library.</summary>
    public IReadOnlyDictionary<string, long>? States { get; init; }

    /// <summary>Names of the folders the files lie in, most files first (for the model).</summary>
    public IReadOnlyList<string> SourceFolders { get; init; } = [];

    /// <summary>Words for <c>{occasion}</c> in a new folder name, from a meaningful source folder name; null when none.</summary>
    public string? OccasionWords { get; init; }

    public ContentGroup? Group => LibraryMap.DominantGroup(Types);
}

/// <summary>One signal of a proposal: how strong, how much it weighs, and in words.</summary>
/// <param name="Agrees">The signal speaks for this target.</param>
/// <param name="Strong">An independent signal counted for the certainty (file type never is).</param>
/// <param name="Contradicts">The signal speaks against this target.</param>
public sealed record SignalResult(MatchSignal Signal, double Value, double Weight, bool Agrees, bool Strong, bool Contradicts, string Detail);

/// <summary>Findings of the structure review at a proposed place, and what they mean for the proposal.</summary>
public sealed record WeakPlace(IReadOnlyList<LibraryFinding> Findings, string Note);

/// <summary>An existing folder proposed as the place for a unit.</summary>
/// <param name="Destination">Where the unit ends up, relative to the library root with <c>/</c>.</param>
public sealed record MatchCandidate(
    MatchPlacement Placement,
    LibraryMapEntry Target,
    string Destination,
    double Score,
    ResultCertainty Certainty,
    IReadOnlyList<SignalResult> Signals,
    string Reason,
    WeakPlace? Weak);

/// <summary>A new folder, proposed when no existing folder fits. Data only; nothing is created.</summary>
/// <param name="NameComplete">False when a part of the pattern could not be filled (the occasion is unknown): the name keeps <c>{occasion}</c>.</param>
public sealed record NewFolderProposal(
    LibraryMapEntry Parent,
    string Name,
    AdviceOrigin NameSource,
    bool NameComplete,
    string? Pattern,
    string Destination,
    ResultCertainty Certainty,
    string Reason,
    WeakPlace? Weak);

/// <summary>The answer of the rules: the best existing folders, or a new one.</summary>
/// <param name="Considered">Folders that were scored.</param>
public sealed record MatchResult(IReadOnlyList<MatchCandidate> Candidates, int Considered, NewFolderProposal? NewFolder);

/// <summary>
/// Finds the folders of a library a unit of a source belongs in (https://github.com/crack666/smart_file_organizer/issues/25).
/// Every proposal is scored from signals that can be checked in one sentence (same content already there, period,
/// names, kind of files, kind of the neighbouring folders); the certainty comes from how many independent signals
/// agree, not from a number a model wrote. Rather "no fitting target, a new folder" than a forced hit.
/// </summary>
public static partial class LibraryMatcher
{
    /// <summary>Kinds of findings about how the folders at a place are named.</summary>
    private static readonly LibraryFindingKind[] NamingKinds =
    [
        LibraryFindingKind.NamingInconsistent, LibraryFindingKind.VariantSpellings, LibraryFindingKind.CaseInconsistent,
        LibraryFindingKind.SimilarNames, LibraryFindingKind.SameNameElsewhere
    ];

    /// <summary>Taken off the score of a parent for a new folder when its subfolders are named in several ways.</summary>
    private const double NamingPenalty = 0.10;

    public static MatchResult Match(LibraryMap map, MatchUnit unit, LibraryMatchOptions o, int limit, LibraryWords words)
    {
        var scored = new List<MatchCandidate>();
        var considered = 0;

        foreach (var e in map.Entries)
        {
            if (e.Depth == 0 || e.Dump is not null) continue;
            if (unit.KeepWhole ? !SameFolderName(unit.Name, e.Name) : e.DirectFiles == 0) continue;
            considered++;
            if (Into(e, unit, o, words) is { } c) scored.Add(c);
        }

        if (unit.KeepWhole)
        {
            foreach (var e in map.Entries)
            {
                if (e.Dump is not null || e.Role == FolderRole.CatchAll && e.Depth > 0) continue;
                if (e.Depth > 0 && e.Children.Count == 0) continue;               // a leaf folder is no place for folders
                if (e.Children.Any(c => SameFolderName(unit.Name, c.Name))) continue; // the same folder exists: into
                considered++;
                if (AsSubfolder(e, unit, o) is { } c) scored.Add(c);
            }
        }

        var fitting = scored
            .Where(c => c.Score >= o.MinScore)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => (int)c.Certainty)
            .ThenBy(c => c.Target.RelativePath, StringComparer.Ordinal)
            .ToList();

        if (fitting.Count >= 2 && fitting[0].Score - fitting[1].Score < o.Margin)
        {
            var best = fitting[0];
            fitting[0] = best with
            {
                Certainty = Lower(best.Certainty),
                Reason = best.Reason.TrimEnd('.') + $"; {fitting[1].Target.RelativePath} fits almost as well."
            };
        }

        var top = fitting.Take(limit).ToList();
        var newFolder = top.Count == 0 ? NewFolder(map, unit, o, words) : null;
        return new MatchResult(top, considered, newFolder);
    }

    // ------------------------------------------------------------------ into an existing folder

    private static MatchCandidate? Into(LibraryMapEntry e, MatchUnit unit, LibraryMatchOptions o, LibraryWords words)
    {
        var signals = new List<SignalResult>
        {
            Content(e, unit, o, o.WeightContent, subtree: true),
            Period(e, unit, o, o.WeightPeriod),
            Name(e, unit, o, o.WeightName, words),
            Type(e, unit, o.WeightType)
        };

        var content = signals[0];
        // A type that does not fit at all rules a folder out, unless the same content already lies there.
        if (signals[3].Contradicts && !content.Agrees) return null;

        var weak = WeakAt(e, unit, MatchPlacement.IntoExisting);
        var certainty = Certainty(signals, weak.Cap);
        var destination = e.RelativePath;
        return new MatchCandidate(MatchPlacement.IntoExisting, e, destination, Score(signals), certainty, signals,
            Reason($"Into {e.RelativePath}", signals), weak.Place);
    }

    // ------------------------------------------------------------------ a named folder next to its kind

    private static MatchCandidate? AsSubfolder(LibraryMapEntry p, MatchUnit unit, LibraryMatchOptions o)
    {
        var signals = SubfolderSignals(p, unit, o);
        if (signals.Single(s => s.Signal == MatchSignal.FileType).Contradicts && !signals[0].Agrees) return null;

        // Content of the folder already lies below here under another name: the person decides whether it is the same.
        var elsewhere = SameContentUnderOtherName(p, unit);
        var weak = WeakAt(p, unit, MatchPlacement.AsSubfolder);
        var certainty = Certainty(signals, weak.Cap);
        var reason = Reason($"As '{unit.Name}' into {p.RelativePath}", signals);
        if (elsewhere is not null)
        {
            certainty = ResultCertainty.Review;
            reason = reason.TrimEnd('.') + $"; files of it already lie in {elsewhere} under another name: is it the same occasion?";
        }
        return new MatchCandidate(MatchPlacement.AsSubfolder, p, Join(p.RelativePath, unit.Name), Score(signals), certainty,
            signals, reason, weak.Place);
    }

    private static List<SignalResult> SubfolderSignals(LibraryMapEntry p, MatchUnit unit, LibraryMatchOptions o) =>
    [
        Content(p, unit, o, o.SubfolderWeightContent, subtree: true),
        Siblings(p, unit, o, o.SubfolderWeightSiblings),
        Type(p, unit, o.SubfolderWeightType),
        SiblingPeriod(p, unit, o.SubfolderWeightPeriod),
        Habit(p, unit, o.SubfolderWeightName)
    ];

    private static string? SameContentUnderOtherName(LibraryMapEntry p, MatchUnit unit)
    {
        if (unit.MatchDirectories.Count == 0) return null;
        var byChild = p.Children
            .Select(c => (Child: c, Count: unit.MatchDirectories.Count(d => d == c.FullPath || IsBelow(d, c.FullPath))))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();
        return byChild.Child is not null && (double)byChild.Count / Math.Max(1, unit.Files) >= 0.1 ? byChild.Child.RelativePath : null;
    }

    // ------------------------------------------------------------------ new folder

    private static NewFolderProposal NewFolder(LibraryMap map, MatchUnit unit, LibraryMatchOptions o, LibraryWords words)
    {
        // The parent: the folder whose subfolders are most like the unit; the root when none is.
        var parents = map.Entries
            .Where(e => e.Dump is null && (e.Depth == 0 || e.Children.Count > 0 && e.Role != FolderRole.CatchAll))
            .Select(e => (Entry: e, Signals: SubfolderSignals(e, unit, o)))
            .Where(x => !x.Signals.Single(s => s.Signal == MatchSignal.FileType).Contradicts)
            // Where the neighbours are named in several ways, the habit for a new name is unclear: a weaker home.
            .Select(x => (x.Entry, x.Signals,
                Score: Score(x.Signals) - (x.Entry.Findings.Any(f => NamingKinds.Contains(f.Kind)) ? NamingPenalty : 0)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Depth)
            .ThenBy(x => x.Entry.RelativePath, StringComparer.Ordinal)
            .ToList();
        var best = parents.FirstOrDefault(x => x.Signals.Any(s => s.Signal is MatchSignal.Siblings or MatchSignal.FileType && s.Agrees));
        var parent = best.Entry ?? map.Root;
        var signals = best.Signals ?? [];

        string name;
        bool complete;
        string? pattern;
        if (unit.KeepWhole)
        {
            name = unit.Name;
            complete = true;
            pattern = null;
        }
        else
        {
            pattern = parent.Pattern ?? DefaultPattern(unit);
            (name, complete) = Fill(pattern, unit);
            name = Unique(name, parent);
        }

        var siblingsAgree = signals.Any(s => s.Signal == MatchSignal.Siblings && s.Strong);
        var certainty = parent.Depth > 0 && siblingsAgree ? ResultCertainty.Uncertain : ResultCertainty.Review;
        var weak = WeakAt(parent, unit, MatchPlacement.NewFolder);
        if (weak.Cap is { } cap && (int)cap > (int)certainty) certainty = cap;

        var where = parent.Depth == 0 ? "at the top of the library" : $"in {parent.RelativePath}";
        var why = unit.KeepWhole
            ? $"No folder of the library holds folders like this one well enough; it keeps its name and goes {where}"
            : $"No existing folder fits well enough; a new folder {where}"
              + (pattern is null ? string.Empty : $", named like its neighbours ('{pattern}')")
              + (complete ? string.Empty : "; the occasion is unknown and stays to be filled in");
        var agreeing = signals.Where(s => s.Agrees).Select(s => s.Detail).ToList();
        var reason = why + (agreeing.Count > 0 ? ": " + string.Join("; ", agreeing) : string.Empty) + ".";

        return new NewFolderProposal(parent, name, AdviceOrigin.Rule, complete, pattern, Join(parent.RelativePath, name),
            certainty, reason, weak.Place);
    }

    private static string DefaultPattern(MatchUnit unit) => unit.Group switch
    {
        ContentGroup.Media => "{occasion} {year}",
        _ when unit.From is not null => "{name} {year}",
        _ => "{name}"
    };

    /// <summary>A name that no sibling has yet: <c>Name (2)</c> otherwise.</summary>
    private static string Unique(string name, LibraryMapEntry parent)
    {
        var taken = parent.Children.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Fills a naming pattern with what is known about the unit: <c>{year}</c>, <c>{date}</c>, <c>{month}</c> from
    /// its period, <c>{occasion}</c> or <c>{name}</c> from a meaningful source folder name. German placeholders of
    /// <c>library propose --lang de</c> are understood. What cannot be filled stays as it is.
    /// </summary>
    public static (string Name, bool Complete) Fill(string pattern, MatchUnit unit)
    {
        var complete = true;
        var name = PlaceholderRegex().Replace(pattern, m =>
        {
            var value = Value(m.Groups[1].Value.Trim().ToLowerInvariant(), unit);
            if (value is null) complete = false;
            return value ?? m.Value;
        });
        foreach (var c in Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).Distinct())
            name = name.Replace(c.ToString(), string.Empty);
        name = SpacesRegex().Replace(name, " ").Trim(' ', '-', '_', '.');
        return (name.Length == 0 ? "New folder" : name, complete && name.Length > 0);
    }

    private static string? Value(string placeholder, MatchUnit unit)
    {
        var from = unit.From;
        var to = unit.To ?? from;
        return placeholder switch
        {
            "year" or "jahr" when from is { } f && to is { } t => f.Year == t.Year
                ? f.Year.ToString(CultureInfo.InvariantCulture)
                : $"{f.Year}-{t.Year}",
            "date" or "datum" when from is { } f => f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "date_compact" when from is { } f => f.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            "date_dmy" when from is { } f => f.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            "month" or "monat" when from is { } f => f.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            "occasion" or "anlass" or "name" or "title" or "titel" or "area" or "bereich" or "topic" or "thema" => unit.OccasionWords,
            _ => null
        };
    }

    // ------------------------------------------------------------------ signals

    private static SignalResult Content(LibraryMapEntry e, MatchUnit unit, LibraryMatchOptions o, double weight, bool subtree)
    {
        if (unit.States is null)
            return new SignalResult(MatchSignal.ContentNeighbours, 0, weight, false, false, false,
                "not compared with this library (sfo compare)");
        var n = unit.MatchDirectories.Count(d => d == e.FullPath || subtree && IsBelow(d, e.FullPath));
        var share = unit.Files == 0 ? 0 : (double)n / unit.Files;
        var agrees = n > 0 && share >= o.MinContentShare;
        var detail = n == 0
            ? "no file of it lies here yet"
            : $"{n} of {unit.Files} files already lie {(e.Depth == 0 ? "in the library" : "here")} with the same content";
        return new SignalResult(MatchSignal.ContentNeighbours, Math.Min(1, share / o.FullContentShare), weight, agrees, agrees, false, detail);
    }

    private static SignalResult Period(LibraryMapEntry e, MatchUnit unit, LibraryMatchOptions o, double weight)
    {
        if (unit.From is not { } uf || unit.To is not { } ut)
            return new SignalResult(MatchSignal.Period, 0, weight, false, false, false, "the files have no usable time");

        var tolerance = TimeSpan.FromDays(o.PeriodToleranceDays);
        var reliableUnit = unit.TimeSource is not Domain.Enums.TimeSource.ModifiedDate;
        var np = e.NamePeriod;

        // The folder's own times count when they rest on names, or when they agree with the year the name states;
        // modification times alone may be the time of a copy.
        var filesKnown = e.From is not null && e.To is not null;
        var filesReliable = filesKnown && (e.TimeSource is Domain.Enums.TimeSource.FileName or Domain.Enums.TimeSource.Mixed
                                           || np is null || Overlaps(e.From!.Value, e.To!.Value, np.From, np.To, TimeSpan.Zero));
        var unitText = Range(uf, ut);

        if (filesReliable && Overlaps(uf, ut, e.From!.Value, e.To!.Value, tolerance))
            return new SignalResult(MatchSignal.Period, 1, weight, true, reliableUnit, false,
                $"taken {unitText}, within the folder's {Range(e.From!.Value, e.To!.Value)}");
        if (np is { DayOrMonth: true } && Overlaps(uf, ut, np.From, np.To, tolerance))
            return new SignalResult(MatchSignal.Period, 1, weight, true, reliableUnit, false,
                $"taken {unitText}, the date in the folder's name");
        if (np is not null && Overlaps(uf, ut, np.From, np.To, TimeSpan.Zero))
            return new SignalResult(MatchSignal.Period, 0.5, weight, true, false, false,
                $"from {Years(uf, ut)}, the year in the folder's name");
        if (filesKnown && Overlaps(uf, ut, new DateTime(e.From!.Value.Year, 1, 1), new DateTime(e.To!.Value.Year, 12, 31, 23, 59, 59), TimeSpan.Zero))
            return new SignalResult(MatchSignal.Period, 0.3, weight, true, false, false,
                $"from {Years(uf, ut)}, as files of the folder");

        var known = np is not null || filesReliable;
        if (!known)
            return new SignalResult(MatchSignal.Period, 0, weight, false, false, false, "the folder has no usable time");
        var folderText = np is not null ? Range(np.From, np.To) : Range(e.From!.Value, e.To!.Value);
        // An occasion or a year is bound to its time; a kind of documents collects over the years.
        var bound = e.Role is FolderRole.Occasion or FolderRole.Time || np is not null;
        return new SignalResult(MatchSignal.Period, 0, weight, false, false, bound && reliableUnit,
            $"taken {unitText}, outside the folder's {folderText}");
    }

    private static SignalResult Name(LibraryMapEntry e, MatchUnit unit, LibraryMatchOptions o, double weight, LibraryWords words)
    {
        if (unit.Words.Count == 0)
            return new SignalResult(MatchSignal.Name, 0, weight, false, false, false, "no words in the names to compare");
        // A year folder is named by its parent: Invoices/2016 is about invoices.
        var folderWords = LibraryMap.Words(e.Name, words);
        if (folderWords.Count == 0 && e.Parent is { Depth: > 0 } parent)
            folderWords = LibraryMap.Words(parent.Name, words);
        var matched = unit.Words.Where(w => folderWords.Any(f => LibraryMap.SameWord(w, f))).ToList();
        var share = (double)matched.Count / unit.Words.Count;
        var agrees = matched.Count > 0 && share >= o.MinNameShare;
        var detail = matched.Count == 0
            ? $"no word of '{string.Join(" ", unit.Words)}' in the folder's name"
            : $"'{string.Join(" ", matched)}' in the folder's name";
        return new SignalResult(MatchSignal.Name, share, weight, agrees, agrees, false, detail);
    }

    private static SignalResult Type(LibraryMapEntry e, MatchUnit unit, double weight)
    {
        if (unit.Group is not { } group || e.Files == 0)
            return new SignalResult(MatchSignal.FileType, 0, weight, false, false, false, "nothing to compare by file type");
        var share = e.Share(group);
        var value = share >= 0.5 ? 1 : share >= 0.1 ? 0.5 : 0;
        return new SignalResult(MatchSignal.FileType, value, weight, value >= 1, false, share < 0.1 && e.Files >= 3,
            $"{LibraryText.Percent(share)} of its files are {LibraryMap.GroupWords(group)}");
    }

    private static SignalResult Siblings(LibraryMapEntry p, MatchUnit unit, LibraryMatchOptions o, double weight)
    {
        var children = p.Children.Where(c => c.Files > 0 || c.Dump is not null).ToList();
        if (children.Count == 0)
            return new SignalResult(MatchSignal.Siblings, 0, weight, false, false, false, "no folders with content here");
        var same = children.Where(c => c.Role == unit.Role).ToList();
        var share = (double)same.Count / children.Count;
        var agrees = same.Count > 0 && share >= o.MinSiblingShare;
        var examples = same.Take(2).Select(c => c.Name).ToList();
        var detail = same.Count == 0
            ? $"none of the {children.Count} folders here are {LibraryMap.RoleWords(unit.Role)}"
            : $"{same.Count} of {children.Count} folders here are {LibraryMap.RoleWords(unit.Role)} ({string.Join(", ", examples)})";
        return new SignalResult(MatchSignal.Siblings, share, weight, agrees, agrees, false, detail);
    }

    private static SignalResult SiblingPeriod(LibraryMapEntry p, MatchUnit unit, double weight)
    {
        if (unit.From is not { } uf || unit.To is not { } ut)
            return new SignalResult(MatchSignal.Period, 0, weight, false, false, false, "the files have no usable time");
        if (p.Role == FolderRole.Time && p.NamePeriod is { } own)
        {
            var inside = Overlaps(uf, ut, own.From, own.To, TimeSpan.Zero);
            return new SignalResult(MatchSignal.Period, inside ? 1 : 0, weight, inside, inside, inside ? false : true,
                inside ? $"from {Years(uf, ut)}, the year of this folder" : $"from {Years(uf, ut)}, not the year of this folder");
        }
        var years = p.Children.Where(c => c.NamePeriod is not null).Select(c => c.NamePeriod!).ToList();
        if (years.Count == 0)
            return new SignalResult(MatchSignal.Period, 0, weight, false, false, false, "the folders here state no years");
        var min = years.Min(y => y.From.Year);
        var max = years.Max(y => y.To.Year);
        var within = uf.Year >= min - 1 && ut.Year <= max + 1;
        return new SignalResult(MatchSignal.Period, within ? 0.5 : 0, weight, within, false, false,
            within ? $"from {Years(uf, ut)}, the folders here span {min} to {max}" : $"from {Years(uf, ut)}, the folders here span {min} to {max}");
    }

    private static SignalResult Habit(LibraryMapEntry p, MatchUnit unit, double weight)
    {
        if (p.ChildrenPattern is not { } habit || !unit.KeepWhole)
            return new SignalResult(MatchSignal.Name, 0, weight, false, false, false, "no naming habit to compare");
        var follows = LibraryText.Classify(unit.Name) == habit.NamePattern;
        return new SignalResult(MatchSignal.Name, follows ? 1 : 0, weight, follows, false, false,
            follows
                ? $"the name follows the habit here ('{p.Pattern}')"
                : $"the habit here is '{p.Pattern}'; the folder keeps its own name");
    }

    // ------------------------------------------------------------------ certainty and weak places

    /// <summary>
    /// <c>certain</c>: same content already there plus another independent signal. <c>likely</c>: two independent
    /// signals, or one plus the right kind of files. <c>uncertain</c>: less. <c>review</c>: a signal contradicts.
    /// </summary>
    public static ResultCertainty Certainty(IReadOnlyList<SignalResult> signals, ResultCertainty? cap)
    {
        ResultCertainty c;
        if (signals.Any(s => s.Contradicts))
            c = ResultCertainty.Review;
        else
        {
            var strong = signals.Count(s => s.Strong && s.Signal != MatchSignal.FileType);
            var content = signals.Any(s => s.Signal == MatchSignal.ContentNeighbours && s.Strong);
            var type = signals.Any(s => s.Signal == MatchSignal.FileType && s.Agrees);
            c = content && strong >= 2 ? ResultCertainty.Certain
                : strong >= 2 || strong >= 1 && type ? ResultCertainty.Likely
                : ResultCertainty.Uncertain;
        }
        return cap is { } limit && (int)limit > (int)c ? limit : c;
    }

    private static ResultCertainty Lower(ResultCertainty c) => c switch
    {
        ResultCertainty.Certain => ResultCertainty.Likely,
        ResultCertainty.Likely => ResultCertainty.Uncertain,
        _ => c
    };

    /// <summary>
    /// The findings at a place and how far they limit the certainty: a high-impact finding (placeholder name, files
    /// that do not fit the name) means a person should look; a medium one caps the certainty at <c>uncertain</c>.
    /// For a named folder that keeps its name, findings about how the neighbours are named are only a note.
    /// </summary>
    private static (WeakPlace? Place, ResultCertainty? Cap) WeakAt(LibraryMapEntry at, MatchUnit unit, MatchPlacement placement)
    {
        var findings = at.Findings;
        if (findings.Count == 0) return (null, null);

        ResultCertainty? cap = null;
        var notes = new List<string>();
        foreach (var f in findings.OrderBy(f => f.Impact))
        {
            var naming = NamingKinds.Contains(f.Kind);
            if (naming && placement == MatchPlacement.AsSubfolder)
                continue;
            if (naming && placement == MatchPlacement.NewFolder)
            {
                cap = Max(cap, ResultCertainty.Uncertain);
                continue;
            }
            if (f.Impact == FindingImpact.High) cap = Max(cap, ResultCertainty.Review);
            else if (f.Impact == FindingImpact.Medium) cap = Max(cap, ResultCertainty.Uncertain);
        }

        var codes = findings.Select(f => f.Kind.ToCode()).Distinct().ToList();
        notes.Add($"The order at {(at.Depth == 0 ? "the top" : at.RelativePath)} is weak ({string.Join(", ", codes)})");
        if (findings.Any(f => NamingKinds.Contains(f.Kind)))
        {
            notes.Add(placement switch
            {
                MatchPlacement.AsSubfolder => "the folders there are named in several ways; this one keeps its own name",
                MatchPlacement.NewFolder => at.Pattern is { } p
                    ? $"the folders there are named in several ways; the new name follows the most common one with a date ('{p}')"
                    : "the folders there are named in several ways",
                _ => "the folders there are named in several ways"
            });
        }
        if (cap == ResultCertainty.Review)
            notes.Add("a person should look before anything goes there");
        else if (cap == ResultCertainty.Uncertain)
            notes.Add("the existing order there is not continued blindly");
        return (new WeakPlace(findings, string.Join("; ", notes) + "."), cap);
    }

    private static ResultCertainty Max(ResultCertainty? a, ResultCertainty b) =>
        a is { } x && (int)x > (int)b ? x : b;

    // ------------------------------------------------------------------ helpers

    private static double Score(IReadOnlyList<SignalResult> signals) =>
        Math.Round(signals.Sum(s => s.Value * s.Weight), 3);

    private static string Reason(string head, IReadOnlyList<SignalResult> signals)
    {
        var pro = signals.Where(s => s.Agrees).Select(s => s.Detail).ToList();
        var contra = signals.Where(s => s.Contradicts).Select(s => s.Detail).ToList();
        var text = head + ": " + (pro.Count > 0 ? string.Join("; ", pro) : "only weak signals");
        if (contra.Count > 0) text += "; but " + string.Join("; ", contra);
        return text + ".";
    }

    /// <summary>The same folder: the same letters (case, umlauts and separators aside) and the same years.</summary>
    public static bool SameFolderName(string a, string b) =>
        LibraryText.NameKey(a) == LibraryText.NameKey(b) && LibraryText.NameKey(a).Length > 0
        && LibraryText.Years(a).Order().SequenceEqual(LibraryText.Years(b).Order());

    private static bool IsBelow(string path, string folder) =>
        path.Length > folder.Length
        && path.StartsWith(folder, StringComparison.Ordinal)
        && path[folder.Length] is '/' or '\\';

    private static bool Overlaps(DateTime aFrom, DateTime aTo, DateTime bFrom, DateTime bTo, TimeSpan tolerance) =>
        aFrom <= bTo + tolerance && bFrom <= aTo + tolerance;

    private static string Range(DateTime from, DateTime to) =>
        from.Date == to.Date ? LibraryMap.Day(from) : $"{LibraryMap.Day(from)} to {LibraryMap.Day(to)}";

    private static string Years(DateTime from, DateTime to) =>
        from.Year == to.Year ? from.Year.ToString(CultureInfo.InvariantCulture) : $"{from.Year} to {to.Year}";

    private static string Join(string parent, string name) => parent == "." ? name : $"{parent}/{name}";

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRegex();
}
