using System.Security.Cryptography;
using System.Text;
using SmartFileOrganizer.Application.Bundles;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Findings;

/// <summary>What a file is worth to the person, as far as the stored results tell.</summary>
public enum FileWorth
{
    /// <summary>Not counted: the results say nothing about it (a font, a log file without a verdict).</summary>
    None,

    /// <summary>Worth keeping.</summary>
    Keep,

    /// <summary>Can go or can be recreated.</summary>
    Disposable
}

/// <summary>The worth of one file and how far it can be relied on, with the step it rests on.</summary>
public readonly record struct FileValue(FileWorth Worth, ResultCertainty Certainty, string Basis)
{
    public static FileValue None { get; } = new(FileWorth.None, ResultCertainty.Review, "none");
}

/// <summary>Where a file lies, as far as the findings care: the bundle around it and the verdict of its folder.</summary>
public sealed record FilePlace(Bundle? Bundle, Bundle? Part, DirectoryReadRow? Directory, CaptureInfo? Capture, bool NoiseByBundle, bool NoiseByDirectory);

/// <summary>A file with its place, its worth as the findings use it, and its stored facts (input of <c>sfo decide</c>).</summary>
public sealed record FileValueRow(FileNode File, FilePlace Place, FileValue Value, IReadOnlyList<FileFact> Facts);

/// <summary>
/// The pure rules of the findings (https://github.com/crack666/smart_file_organizer/issues/26): what a file is worth,
/// whether it lies among noise, how certain a finding is, its key and its place in the list. No database, no model.
/// </summary>
public static class FindingRules
{
    /// <summary>Order of the kinds within the sure and the unsure part: what can be lost first, what is only untidy last.</summary>
    public static IReadOnlyList<FindingKind> KindOrder { get; } =
    [
        FindingKind.NotBackedUp, FindingKind.InNoise, FindingKind.Sensitive, FindingKind.Misplaced, FindingKind.Duplicate
    ];

    private static readonly FileCategory[] KeptCategories =
    [
        FileCategory.Photo, FileCategory.Video, FileCategory.DocumentScan, FileCategory.Document,
        FileCategory.Invoice, FileCategory.PersonalDocument, FileCategory.Letter
    ];

    private static readonly FileCategory[] KeptWhenImportant =
    [
        FileCategory.Screenshot, FileCategory.Audio, FileCategory.Archive, FileCategory.Code
    ];

    private static readonly FileCategory[] DisposableCategories =
    [
        FileCategory.TrashCandidate, FileCategory.SoftwareInstaller, FileCategory.SystemFile
    ];

    /// <summary>Extensions of documents a person writes; a program folder rarely ships them.</summary>
    private static readonly HashSet<string> PersonalDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".odt", ".rtf", ".pdf", ".xls", ".xlsx", ".ods"
    };

    /// <summary>Name words of documents that come with software, not from a person.</summary>
    private static readonly string[] ShippedDocumentWords =
    [
        "readme", "license", "licence", "changelog", "manual", "eula", "help", "install", "notice", "copying",
        "authors", "release", "third", "credits"
    ];

    /// <summary>
    /// What a file is worth. A human override is certain. A result of the model counts with its own certainty
    /// (older rows without one: likely from a confidence of 0.8, otherwise uncertain). Without a result, the bundle
    /// and the folder verdict decide, and never above <see cref="ResultCertainty.Likely"/>.
    /// </summary>
    public static FileValue ValueOf(FileNode file, FilePlace place)
    {
        if (file.Override is { } human)
        {
            var worth = WorthOfCategory(human.OverriddenCategory, Importance.Unknown);
            return new FileValue(worth == FileWorth.None ? FileWorth.Keep : worth, ResultCertainty.Certain, "human");
        }

        if (file.Classification is { Error: null } c && (c.Category != FileCategory.Unknown || c.KeepHint is not null))
        {
            var certainty = c.Certainty ?? (c.Confidence >= 0.8 ? ResultCertainty.Likely : ResultCertainty.Uncertain);
            var basis = c.Origin is { } origin ? origin.ToCode() : "model";
            if (c.KeepHint == KeepHint.WorthKeeping) return new FileValue(FileWorth.Keep, certainty, basis);
            if (c.KeepHint == KeepHint.Temporary) return new FileValue(FileWorth.Disposable, certainty, basis);
            // "Look at it" is no sign of worth. It counts (as review) only for the kinds a person keeps, and never
            // among noise: there a database or cache file the rules sent to review would fill the list.
            if (c.Category is FileCategory.ReviewNeeded or FileCategory.Unknown)
                return place.NoiseByBundle || place.NoiseByDirectory
                       || file.FileType is not (FileType.Image or FileType.Video or FileType.Document or FileType.Audio)
                    ? FileValue.None
                    : new FileValue(FileWorth.Keep, ResultCertainty.Review, basis);
            var w = WorthOfCategory(c.Category, c.Importance);
            return w == FileWorth.None ? FileValue.None : new FileValue(w, certainty, basis);
        }

        return ValueWithoutResult(file, place);
    }

    private static FileWorth WorthOfCategory(FileCategory category, Importance importance)
    {
        if (Array.IndexOf(KeptCategories, category) >= 0)
            return importance == Importance.Low ? FileWorth.Disposable : FileWorth.Keep;
        if (Array.IndexOf(KeptWhenImportant, category) >= 0)
            return importance >= Importance.High ? FileWorth.Keep : FileWorth.None;
        if (Array.IndexOf(DisposableCategories, category) >= 0) return FileWorth.Disposable;
        return FileWorth.None;
    }

    private static FileValue ValueWithoutResult(FileNode file, FilePlace place)
    {
        if (file.FileType is FileType.Executable or FileType.Font)
            return new FileValue(FileWorth.Disposable, ResultCertainty.Likely, "file_type");

        var media = file.FileType is FileType.Image or FileType.Video;

        // Among noise only what is clearly personal counts, and never above uncertain.
        if (place.NoiseByBundle || place.NoiseByDirectory)
            return IsCameraFile(file, place.Capture) || IsPersonalDocument(file)
                ? new FileValue(FileWorth.Keep, ResultCertainty.Uncertain, "name")
                : FileValue.None;

        if (place.Directory?.Retention is { } retention)
        {
            if (retention == RetentionCode(RetentionValue.Keep))
                return new FileValue(FileWorth.Keep, ResultCertainty.Likely, "directory");
            if (retention == RetentionCode(RetentionValue.Disposable))
                return new FileValue(FileWorth.Disposable, ResultCertainty.Likely, "directory");
        }

        var bundle = place.Part ?? place.Bundle;
        switch (bundle?.Kind)
        {
            case BundleKind.DumpKeep when media && bundle.Part is DumpPart.Camera or DumpPart.Pictures or DumpPart.Videos:
                return new FileValue(FileWorth.Keep, ResultCertainty.Likely, "bundle");
            case BundleKind.DumpKeep:
                return file.FileType is FileType.Image or FileType.Video or FileType.Document or FileType.Audio
                    ? new FileValue(FileWorth.Keep, ResultCertainty.Uncertain, "bundle")
                    : FileValue.None;
            case BundleKind.Event or BundleKind.Scattered when media:
                return new FileValue(FileWorth.Keep,
                    IsCameraFile(file, place.Capture) ? ResultCertainty.Likely : ResultCertainty.Uncertain, "bundle");
        }

        return file.FileType is FileType.Image or FileType.Video or FileType.Document or FileType.Audio
            ? new FileValue(FileWorth.Keep, ResultCertainty.Uncertain, "file_type")
            : FileValue.None;
    }

    /// <summary>A photo or video a camera took: a device in the metadata, or a camera file name with a date (not a screenshot).</summary>
    public static bool IsCameraFile(FileNode file, CaptureInfo? capture)
    {
        if (file.FileType is not (FileType.Image or FileType.Video)) return false;
        if (capture?.ImageOrigin == ImageOrigin.Screenshot) return false;
        if (file.Name.Contains("screenshot", StringComparison.OrdinalIgnoreCase)) return false;
        if (capture?.ImageOrigin == ImageOrigin.Camera || !string.IsNullOrWhiteSpace(capture?.Device)) return true;
        return FileNameDates.TryParse(file.Name) is not null;
    }

    /// <summary>An office document or PDF whose name does not say it came with software.</summary>
    public static bool IsPersonalDocument(FileNode file)
    {
        if (!PersonalDocumentExtensions.Contains(file.Extension)) return false;
        var stem = Path.GetFileNameWithoutExtension(file.Name);
        return !ShippedDocumentWords.Any(w => stem.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A copied program folder: a software dump recognised by programs and libraries alone (no project markers).</summary>
    public static bool IsProgramFolder(Bundle bundle) =>
        bundle is { Kind: BundleKind.Dump, Origin: DumpOrigin.Software }
        && bundle.Facts.Markers.Count > 0
        && bundle.Facts.Markers.All(m => m.StartsWith("file~exe:", StringComparison.Ordinal)
                                         || m.StartsWith("file~dll:", StringComparison.Ordinal));

    /// <summary>A folder verdict that makes the folder a place without value: mixed (not raised from uniform by a sample) or disposable.</summary>
    public static string? DirectoryNoise(DirectoryReadRow? dir)
    {
        if (dir is null) return null;
        if (dir.Retention == RetentionCode(RetentionValue.Disposable)) return "judged disposable";
        if (dir.Verdict == VerdictCodes.ToCode(DirectoryVerdictKind.Mixed) && dir.EscalatedFrom is null) return "judged mixed";
        return null;
    }

    public static string RetentionCode(RetentionValue kind) => VerdictCodes.ToCode(kind);

    /// <summary>
    /// How certain a sensitive mark is: a header line, or rule and model together, is certain; a name or folder rule
    /// alone likely; the model alone as certain as its result.
    /// </summary>
    public static ResultCertainty SensitiveCertainty(string? rules, ResultCertainty? modelCertainty)
    {
        var ids = SplitRules(rules);
        var byModel = ids.Contains("model");
        var byRule = ids.Any(i => i != "model");
        if (ids.Any(i => i.StartsWith("header:", StringComparison.Ordinal))) return ResultCertainty.Certain;
        if (byRule && byModel) return ResultCertainty.Certain;
        if (byRule) return ResultCertainty.Likely;
        return modelCertainty ?? ResultCertainty.Uncertain;
    }

    public static IReadOnlyList<string> SplitRules(string? rules) =>
        string.IsNullOrEmpty(rules)
            ? []
            : rules.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The weaker of two certainties (<see cref="ResultCertainty.Review"/> is the weakest).</summary>
    public static ResultCertainty Weaker(ResultCertainty a, ResultCertainty b) => (int)a >= (int)b ? a : b;

    public static ResultCertainty Weakest(IEnumerable<ResultCertainty> values, ResultCertainty fallback)
    {
        var any = false;
        var result = ResultCertainty.Certain;
        foreach (var v in values)
        {
            any = true;
            result = Weaker(result, v);
        }
        return any ? result : fallback;
    }

    public static bool IsSure(ResultCertainty c) => c is ResultCertainty.Certain or ResultCertainty.Likely;

    /// <summary>
    /// The certainty a <c>misplaced</c> finding may have. It may only rest on a target whose content was checked
    /// (https://github.com/crack666/smart_file_organizer/issues/56): until a check says the content fits, it stays
    /// uncertain at best, however sure the file results are.
    /// </summary>
    public static ResultCertainty MisplacedCertainty(ResultCertainty files, FindingTarget? target)
    {
        var cap = target is { ContentCheck: TargetContentCheck.Fits } ? target.Certainty : ResultCertainty.Uncertain;
        return Weaker(files, cap);
    }

    /// <summary>
    /// Stable key of a finding: kind code plus the first ten hex digits of the SHA-256 of its subject. The subject is
    /// built from relative paths and codes only, so the key survives a new <c>sfo bundles build</c> and a rescan of the same tree.
    /// </summary>
    public static string Key(FindingKind kind, string subject)
    {
        var code = kind.ToCode();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code + "\n" + subject));
        return code + "-" + Convert.ToHexStringLower(hash)[..10];
    }

    /// <summary>Orders findings and sets <see cref="Finding.Rank"/>: sure before unsure, then by kind, certainty, files, bytes, key.</summary>
    public static IReadOnlyList<Finding> Rank(IEnumerable<Finding> findings)
    {
        var ordered = findings
            .OrderBy(f => f.Unsure)
            .ThenBy(f => KindIndex(f.Kind))
            .ThenBy(f => (int)f.Certainty)
            .ThenByDescending(f => f.Files)
            .ThenByDescending(f => f.Bytes)
            .ThenBy(f => f.Key, StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Rank = i + 1;
        return ordered;
    }

    private static int KindIndex(FindingKind kind)
    {
        for (var i = 0; i < KindOrder.Count; i++)
            if (KindOrder[i] == kind) return i;
        return KindOrder.Count;
    }

    /// <summary>The group of file types <c>misplaced</c> compares: media, documents, audio; null for every other type.</summary>
    public static string? KindGroup(FileType type) => type switch
    {
        FileType.Image or FileType.Video => "media",
        FileType.Document => "document",
        FileType.Audio => "audio",
        _ => null
    };

    /// <summary>
    /// The kind of files a <c>not_backed_up</c> or <c>in_noise</c> finding is about: <c>documents</c>, <c>images</c>,
    /// <c>videos</c>, <c>audio</c>, <c>archives</c>, <c>installers</c> or <c>other</c>. A mixed folder gives one finding per
    /// kind, because "132 of 648 files" cannot be accepted or rejected as a whole.
    /// </summary>
    public static string GroupOf(FileType type) => type switch
    {
        FileType.Document => "documents",
        FileType.Image => "images",
        FileType.Video => "videos",
        FileType.Audio => "audio",
        FileType.Archive => "archives",
        FileType.Executable => "installers",
        _ => "other"
    };

    /// <summary>Order of the groups within one place, as a person goes through a download folder.</summary>
    public static int GroupIndex(string group) => group switch
    {
        "documents" => 0,
        "images" => 1,
        "videos" => 2,
        "audio" => 3,
        "archives" => 4,
        "installers" => 5,
        _ => 6
    };

    /// <summary>A folder verdict whose files belong to a whole (a program, a project) or to the system: <c>unit</c> or <c>system</c>.</summary>
    public static bool IsWholeVerdict(string? verdict) =>
        verdict == VerdictCodes.ToCode(DirectoryVerdictKind.Unit) || verdict == VerdictCodes.ToCode(DirectoryVerdictKind.System);

    /// <summary>Whether the worth of a file rests on a judgement of the file itself (person, agent, model, rule, a confirmed sample, its described facts).</summary>
    public static bool RestsOnFile(FileValue value) =>
        value.Basis is "human" or "agent" or "model" or "rule" or "inherited" or "facts";

    /// <summary>Relative path with <c>/</c>; <c>.</c> for the root.</summary>
    public static string Rel(string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return ".";
        var r = relative.Replace('\\', '/').Trim('/');
        return r.Length == 0 ? "." : r;
    }

    /// <summary>"1 file" / "3 files".</summary>
    public static string Count(long n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    /// <summary>Bytes for a sentence, as the read commands write them: 1.2 GB, 340 MB, 12 KB.</summary>
    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + units[u];
    }
}
