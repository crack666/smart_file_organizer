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
/// <param name="Content">The content check of this candidate; null when it was not checked.</param>
public sealed record MatchCandidate(
    MatchPlacement Placement,
    LibraryMapEntry Target,
    string Destination,
    double Score,
    ResultCertainty Certainty,
    IReadOnlyList<SignalResult> Signals,
    string Reason,
    WeakPlace? Weak,
    CandidateContent? Content = null);

/// <summary>What the content check said about one candidate, and what the folder shows.</summary>
/// <param name="Shared">The concrete feature both share, as the judge named it (Issue 85); null when none.</param>
public sealed record CandidateContent(ContentFit Fit, string Reason, ContentDescription? Folder, string? Shared = null, SharedFeatureKind? SharedKind = null);

/// <summary>
/// The content check of a match (https://github.com/crack666/smart_file_organizer/issues/56): per candidate whether
/// what the unit shows fits what the folder shows. <see cref="Status"/> says whether it ran; when it did not, every
/// proposal without the same files at the target is at most <c>uncertain</c> and says so.
/// </summary>
/// <param name="ByKey">Per <see cref="KeyOf"/> of a candidate its verdict; candidates not listed were not checked.</param>
public sealed record ContentCheck(ContentCheckStatus Status, ContentDescription? Unit, IReadOnlyDictionary<string, CandidateContent> ByKey, string? Note)
{
    public static string KeyOf(MatchPlacement placement, string relativePath) => $"{placement.ToCode()}:{relativePath}";

    public static ContentCheck NotChecked(ContentCheckStatus status, string note, ContentDescription? unit = null) =>
        new(status, unit, new Dictionary<string, CandidateContent>(), note);

    /// <summary>Without a model: places come from time, names and kinds of files only.</summary>
    public static ContentCheck NoModel { get; } = NotChecked(ContentCheckStatus.NoModel, "no model");
}

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

/// <summary>The answer of the rules: the best existing folders, or a new one, or why there is none.</summary>
/// <param name="Considered">Folders that were scored.</param>
/// <param name="Rejected">Candidates the period or names chose but whose content contradicts the unit; never proposed.</param>
/// <param name="Unconfirmed">
/// Existing folders time, names and kinds of files chose but no content confirms (neither the same files there nor a
/// content check that fits); never proposed (https://github.com/crack666/smart_file_organizer/issues/85).
/// </param>
/// <param name="NoTarget">Why neither an existing nor a new folder is proposed; null when there is a proposal.</param>
public sealed record MatchResult(
    IReadOnlyList<MatchCandidate> Candidates, int Considered, NewFolderProposal? NewFolder,
    IReadOnlyList<MatchCandidate>? Rejected = null, ContentCheck? Content = null,
    IReadOnlyList<MatchCandidate>? Unconfirmed = null, string? NoTarget = null);

/// <summary>
/// What kind of content a unit is, for the choice of the library (https://github.com/crack666/smart_file_organizer/issues/85):
/// own pictures and videos go to the library of photos, films to the one of videos, papers to the one of documents.
/// Internal to the matching; the output says it in words.
/// </summary>
public enum ContentKind
{
    OwnRecordings,
    ForeignMedia,
    Documents,
    Audio,
    Archives,
    Software,
    Other
}

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

    /// <summary>
    /// The candidates time, names and kinds of files choose (score at least <c>MinScore</c>), best first, before any
    /// content check: the folders worth describing.
    /// </summary>
    public static IReadOnlyList<MatchCandidate> Preselect(LibraryMap map, MatchUnit unit, LibraryMatchOptions o, LibraryWords words) =>
        Scored(map, unit, o, words).Candidates
            .Where(c => c.Score >= o.MinScore)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Target.RelativePath, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The best places for <paramref name="unit"/>. Time, names and kinds of files choose the candidates; what decides
    /// is the content (https://github.com/crack666/smart_file_organizer/issues/56): a candidate whose content
    /// contradicts the unit is dropped, even when the period fits; without fitting content (or the same files already
    /// at the target) a proposal is at most <c>uncertain</c>. <paramref name="content"/> null means not checked.
    /// </summary>
    public static MatchResult Match(LibraryMap map, MatchUnit unit, LibraryMatchOptions o, int limit, LibraryWords words, ContentCheck? content = null)
    {
        content ??= ContentCheck.NoModel;
        var (scored, considered) = Scored(map, unit, o, words);

        var fitting = new List<MatchCandidate>();
        var rejected = new List<MatchCandidate>();
        var unconfirmed = new List<MatchCandidate>();
        foreach (var c in scored.Where(c => c.Score >= o.MinScore))
        {
            var judged = ApplyContent(c, unit, o, content);
            if (judged is null) rejected.Add(c with { Content = content.ByKey[ContentCheck.KeyOf(c.Placement, c.Target.RelativePath)] });
            else if (o.RequireContent && !Confirmed(judged, unit)) unconfirmed.Add(judged);
            else fitting.Add(judged);
        }
        unconfirmed = unconfirmed.OrderByDescending(c => c.Score).ThenBy(c => c.Target.RelativePath, StringComparer.Ordinal).ToList();
        fitting = fitting
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
        NewFolderProposal? newFolder = null;
        string? noTarget = null;
        if (top.Count == 0)
        {
            // A new folder only in a library that holds this kind of content; otherwise rather no target.
            var share = KindShare(map, unit);
            if (map.Root.Files > 0 && unit.Files > 0 && share < o.MinLibraryKindShare)
                noTarget = $"No existing folder is confirmed by content, and the library holds no {KindWords(KindOf(unit))} "
                           + $"({LibraryText.Percent(share)} of its files): a new folder there would be a guess as well.";
            else
                newFolder = NewFolder(map, unit, o, words, rejected, unconfirmed);
        }
        return new MatchResult(top, considered, newFolder, rejected, content, unconfirmed, noTarget);
    }

    /// <summary>
    /// Content confirms an existing folder: the same files already lie there, or the content check says it fits. A named
    /// folder that goes into the library folder of the same name and years is the same folder, confirmed by its name.
    /// A new subfolder under the unit's own name is no existing folder and needs no confirmation.
    /// </summary>
    private static bool Confirmed(MatchCandidate c, MatchUnit unit) =>
        c.Placement != MatchPlacement.IntoExisting
        || unit.KeepWhole
        || c.Signals.Any(s => s.Signal == MatchSignal.ContentNeighbours && s.Strong)
        || c.Signals.Any(s => s.Signal == MatchSignal.ContentFit && s.Agrees);

    /// <summary>
    /// What kind of content the unit is: documents, own pictures and videos (pictures, or videos with a capture date or a
    /// date in the name, as cameras and phones write them), or videos from elsewhere such as films.
    /// </summary>
    public static ContentKind KindOf(MatchUnit unit)
    {
        switch (unit.Group)
        {
            case ContentGroup.Media:
                var images = unit.Types[(int)FileType.Image];
                var videos = unit.Types[(int)FileType.Video];
                return images >= videos || unit.TimeSource is Domain.Enums.TimeSource.CaptureDate or Domain.Enums.TimeSource.FileName
                    ? ContentKind.OwnRecordings
                    : ContentKind.ForeignMedia;
            case ContentGroup.Document: return ContentKind.Documents;
            case ContentGroup.Audio: return ContentKind.Audio;
            case ContentGroup.Archive: return ContentKind.Archives;
            case ContentGroup.Software: return ContentKind.Software;
            default: return ContentKind.Other;
        }
    }

    /// <summary>
    /// Share of the library's files that are of the unit's kind of content: for own recordings its pictures and the videos
    /// outside film and episode folders, for films its videos, otherwise the files of the unit's group.
    /// </summary>
    public static double KindShare(LibraryMap map, MatchUnit unit)
    {
        var root = map.Root;
        if (root.Files == 0) return 0;
        var titleVideos = map.Entries.Where(e => e.Closed == ClosedUnitKind.SingleTitle && e.ClosedAt == e.RelativePath)
            .Sum(e => e.Types[(int)FileType.Video]);
        double count = KindOf(unit) switch
        {
            ContentKind.OwnRecordings => root.Types[(int)FileType.Image] + Math.Max(0, root.Types[(int)FileType.Video] - titleVideos),
            ContentKind.ForeignMedia => root.Types[(int)FileType.Video],
            _ when unit.Group is { } g => LibraryMap.Count(root.Types, g),
            _ => 0
        };
        return count / root.Files;
    }

    public static string KindWords(ContentKind kind) => kind switch
    {
        ContentKind.OwnRecordings => "own photos and videos",
        ContentKind.ForeignMedia => "films or other videos",
        ContentKind.Documents => "documents",
        ContentKind.Audio => "audio",
        ContentKind.Archives => "archives",
        ContentKind.Software => "programs and code",
        _ => "files of this kind"
    };

    private static (List<MatchCandidate> Candidates, int Considered) Scored(LibraryMap map, MatchUnit unit, LibraryMatchOptions o, LibraryWords words)
    {
        var scored = new List<MatchCandidate>();
        var considered = 0;

        foreach (var e in map.Entries)
        {
            if (e.Depth == 0 || e.Dump is not null) continue;
            if (unit.KeepWhole ? !SameFolderName(unit.Name, e.Name) : e.DirectFiles == 0) continue;
            // A closed unit (a program, a cache, one film) never takes in loose files from elsewhere
            // (https://github.com/crack666/smart_file_organizer/issues/85); only the same unit under its own name goes there.
            if (e.Closed is not null && !(unit.KeepWhole && e.ClosedAt == e.RelativePath)) continue;
            considered++;
            if (Into(e, unit, o, words) is { } c) scored.Add(c);
        }

        if (unit.KeepWhole)
        {
            foreach (var e in map.Entries)
            {
                if (e.Dump is not null || e.Closed is not null || e.Role == FolderRole.CatchAll && e.Depth > 0) continue;
                if (e.Depth > 0 && e.Children.Count == 0) continue;               // a leaf folder is no place for folders
                if (e.Children.Any(c => SameFolderName(unit.Name, c.Name))) continue; // the same folder exists: into
                considered++;
                if (AsSubfolder(e, unit, o) is { } c) scored.Add(c);
            }
        }
        return (scored, considered);
    }

    // ------------------------------------------------------------------ content

    /// <summary>
    /// Adds the content signal to a candidate and judges it again: null when the content contradicts (and the same
    /// files do not already lie there). The score grows by <c>WeightContentFit</c> for fitting content, so a fitting
    /// folder outranks one that only shares the period.
    /// </summary>
    private static MatchCandidate? ApplyContent(MatchCandidate c, MatchUnit unit, LibraryMatchOptions o, ContentCheck content)
    {
        var sameFiles = c.Signals.Any(s => s.Signal == MatchSignal.ContentNeighbours && s.Strong);
        content.ByKey.TryGetValue(ContentCheck.KeyOf(c.Placement, c.Target.RelativePath), out var verdict);
        if (verdict is { Fit: ContentFit.Contradicts } && !sameFiles) return null;

        var unitGist = content.Unit is { IsEmpty: false } u ? u.Gist() : null;
        var folderGist = verdict?.Folder is { IsEmpty: false } f ? f.Gist() : null;
        var signal = verdict switch
        {
            { Fit: ContentFit.Fits } => new SignalResult(MatchSignal.ContentFit, 1, o.WeightContentFit, true, true, false,
                $"the content fits: {unitGist ?? "the files"}; the folder shows {folderGist ?? "the same"} ({verdict.Reason.TrimEnd('.')})"),
            { Fit: ContentFit.Contradicts } => new SignalResult(MatchSignal.ContentFit, 0, o.WeightContentFit, false, false, true,
                $"the content does not fit: {unitGist ?? "the files"}; the folder shows {folderGist ?? "something else"} ({verdict.Reason.TrimEnd('.')})"),
            { Fit: ContentFit.Unclear } => new SignalResult(MatchSignal.ContentFit, 0, o.WeightContentFit, false, false, false,
                $"the content is unclear ({verdict.Reason.TrimEnd('.')})"),
            _ => new SignalResult(MatchSignal.ContentFit, 0, o.WeightContentFit, false, false, false,
                $"the content was not checked ({NotCheckedWhy(content)})")
        };
        var signals = new List<SignalResult> { signal };
        signals.AddRange(c.Signals);

        var weak = WeakAt(c.Target, unit, c.Placement);
        var certainty = c.Certainty == ResultCertainty.Review ? ResultCertainty.Review : Certainty(signals, weak.Cap);
        var head = c.Placement == MatchPlacement.AsSubfolder ? $"As '{unit.Name}' into {c.Target.RelativePath}" : $"Into {c.Target.RelativePath}";
        var reason = Reason(head, signals);
        if (verdict is null || verdict.Fit == ContentFit.Unclear)
            reason = reason.TrimEnd('.') + $"; {signal.Detail}.";
        // Kept from the first judgement: content of the folder already lies below the place under another name.
        if (c.Reason.Contains("under another name", StringComparison.Ordinal))
            reason = reason.TrimEnd('.') + "; files of it already lie there under another name: is it the same occasion?";
        return c with
        {
            Signals = signals,
            Score = Score(signals),
            Certainty = certainty,
            Reason = reason,
            Content = verdict
        };
    }

    private static string NotCheckedWhy(ContentCheck content) => content.Status switch
    {
        ContentCheckStatus.NoModel => "no model",
        ContentCheckStatus.Unavailable => "the local model is not reachable",
        ContentCheckStatus.NoDescription => "the files are not described yet; sfo analyze describes them",
        ContentCheckStatus.Failed => "the model gave no usable answer",
        _ => "not among the folders described"
    };

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
        // Names count only together with a second signal (https://github.com/crack666/smart_file_organizer/issues/85).
        if (!unit.KeepWhole && signals[2].Value > 0 && !signals.Where((s, i) => i != 2).Any(s => s.Agrees))
            signals[2] = signals[2] with
            {
                Value = 0, Agrees = false, Strong = false,
                Detail = signals[2].Detail + "; nothing else agrees, so the name alone does not count"
            };

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

    private static NewFolderProposal NewFolder(LibraryMap map, MatchUnit unit, LibraryMatchOptions o, LibraryWords words,
        IReadOnlyList<MatchCandidate>? rejected = null, IReadOnlyList<MatchCandidate>? unconfirmed = null)
    {
        // The parent: the folder whose subfolders are most like the unit; the root when none is. Never inside a closed unit.
        var parents = map.Entries
            .Where(e => e.Dump is null && e.Closed is null && (e.Depth == 0 || e.Children.Count > 0 && e.Role != FolderRole.CatchAll))
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
        // An occasion without a folder yet: say which folders the period chose and why their content does not fit.
        foreach (var r in (rejected ?? []).Take(2))
            reason = reason.TrimEnd('.') + $"; {r.Target.RelativePath} fits by time or name, not by content ({r.Content?.Reason.TrimEnd('.') ?? "another occasion"}).";
        // Rather no existing folder than one only time and names chose (Issue 85).
        foreach (var u in (unconfirmed ?? []).Take(2))
            reason = reason.TrimEnd('.') + $"; {u.Target.RelativePath} fits by time, name or kind of files only, and no content confirms it.";

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

        // A named folder that keeps its name is compared by its whole name; there one word is the name.
        if (unit.KeepWhole)
        {
            var whole = matched.Count > 0 && share >= o.MinNameShare;
            return new SignalResult(MatchSignal.Name, share, weight, whole, whole, false,
                matched.Count == 0 ? $"no word of '{string.Join(" ", unit.Words)}' in the folder's name" : $"'{string.Join(" ", matched)}' in the folder's name");
        }

        // Loose files (https://github.com/crack666/smart_file_organizer/issues/85): everyday words do not count, and one word
        // never agrees on its own: a photo called "Sommer ..." has nothing to do with a film called "Ein Sommer in ...".
        // One rare word still helps to choose which folders the content check looks at, but never agrees: only content
        // makes the folder a proposal.
        var meaningful = matched.Where(w => !words.CommonWords.Contains(w)).ToList();
        var meaningfulShare = (double)meaningful.Count / unit.Words.Count;
        var agrees = meaningful.Count >= 2 && meaningfulShare >= o.MinNameShare;
        string detail;
        if (matched.Count == 0)
            detail = $"no word of '{string.Join(" ", unit.Words)}' in the folder's name";
        else if (agrees)
            detail = $"'{string.Join(" ", meaningful)}' in the folder's name";
        else if (meaningful.Count == 0)
            detail = $"only everyday words in common with the folder's name ('{string.Join(" ", matched)}'), which say nothing";
        else
            detail = $"only one word in common with the folder's name ('{string.Join(" ", meaningful)}'), which alone says nothing";
        return new SignalResult(MatchSignal.Name, meaningfulShare >= o.MinNameShare ? meaningfulShare : 0, weight, agrees, agrees, false, detail);
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
        // Loose files with a time also fit next to year folders: they would get one of their own.
        var byYear = !unit.KeepWhole && unit.From is not null && children.Count(c => c.Role == FolderRole.Time) * 2 > children.Count;
        var role = byYear ? FolderRole.Time : unit.Role;
        var same = children.Where(c => c.Role == role).ToList();
        var share = (double)same.Count / children.Count;
        var agrees = same.Count > 0 && share >= o.MinSiblingShare;
        var examples = same.Take(2).Select(c => c.Name).ToList();
        var detail = same.Count == 0
            ? $"none of the {children.Count} folders here are {LibraryMap.RoleWords(role)}"
            : $"{same.Count} of {children.Count} folders here are {LibraryMap.RoleWords(role)} ({string.Join(", ", examples)})";
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
    /// Time and kind of files choose, the content decides (https://github.com/crack666/smart_file_organizer/issues/56):
    /// <c>certain</c> with the same files already there plus another independent signal, or with fitting content and a
    /// fitting period together. <c>likely</c> with the same files and the right kind of files, or with fitting content
    /// and one more signal (name, siblings, kind of files). Everything else, however well period and names agree, is
    /// <c>uncertain</c>; <c>review</c> when a signal contradicts.
    /// </summary>
    public static ResultCertainty Certainty(IReadOnlyList<SignalResult> signals, ResultCertainty? cap)
    {
        ResultCertainty c;
        if (signals.Any(s => s.Contradicts))
            c = ResultCertainty.Review;
        else
        {
            var sameFiles = signals.Any(s => s.Signal == MatchSignal.ContentNeighbours && s.Strong);
            var fits = signals.Any(s => s.Signal == MatchSignal.ContentFit && s.Agrees);
            var period = signals.Any(s => s.Signal == MatchSignal.Period && s.Strong);
            var others = signals.Count(s => s.Strong && s.Signal is not (MatchSignal.FileType or MatchSignal.ContentFit or MatchSignal.ContentNeighbours));
            var type = signals.Any(s => s.Signal == MatchSignal.FileType && s.Agrees);
            c = sameFiles && (others >= 1 || fits) || fits && period ? ResultCertainty.Certain
                : sameFiles && type || fits && (others >= 1 || type) ? ResultCertainty.Likely
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
