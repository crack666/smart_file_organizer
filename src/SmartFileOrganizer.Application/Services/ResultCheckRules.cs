using System.Globalization;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// The decisions of the second check (https://github.com/crack666/smart_file_organizer/issues/23), free of IO:
/// which signals contradict a model answer, whether two answers agree, and how sure the stored result is.
/// </summary>
/// <remarks>
/// Certainty rests on agreement between independent signals, not on the confidence the model states:
/// rule and model, two calls with different evidence, a file and the confirmed sample of its directory.
/// </remarks>
public static class ResultCheckRules
{
    /// <summary>What speaks against a first answer. <see cref="RuleCategory"/> is set when an unambiguous rule
    /// sets the category instead of the model.</summary>
    public sealed record Findings(IReadOnlyList<CheckReason> Reasons, FileCategory? RuleCategory, string? RuleNote)
    {
        /// <summary>A second model call is due: any reason except a rule that already decided.</summary>
        public bool NeedsSecondCall => Reasons.Any(r => r != CheckReason.RuleDisagrees);
    }

    /// <summary>Confidence standing in for a missing one when two answers are combined: no statement, a coin flip.</summary>
    private const double UnstatedConfidence = 0.5;

    /// <summary>A rule result at least this sure counts as certain.</summary>
    private const double CertainRuleConfidence = 0.9;

    public static Findings Find(
        FileNode file,
        IReadOnlyList<FileFact> facts,
        FileClassification answer,
        DirectoryVerdict? verdict,
        SecondCheckOptions options)
    {
        var reasons = new List<CheckReason>();
        var notes = new List<string>();

        if (answer.ConfidenceMissing)
            reasons.Add(CheckReason.MissingConfidence);
        else if (answer.Confidence < options.BelowConfidence)
            reasons.Add(CheckReason.LowConfidence);

        if (options.CheckUnclearCategory && IsUnclear(answer.Category))
            reasons.Add(CheckReason.UnclearCategory);

        if (KindMismatch(file, facts, answer.Category) is { } kindNote)
        {
            reasons.Add(CheckReason.KindMismatch);
            notes.Add(kindNote);
        }

        FileCategory? ruleCategory = null;
        if (RuleOverride(facts, answer.Category) is { } rule)
        {
            reasons.Add(CheckReason.RuleDisagrees);
            ruleCategory = rule.Category;
            notes.Add(rule.Note);
        }

        if (options.CheckVerdictContradictions && VerdictContradiction(answer, verdict) is { } verdictNote)
        {
            reasons.Add(CheckReason.VerdictDisagrees);
            notes.Add(verdictNote);
        }

        return new Findings(reasons, ruleCategory, notes.Count == 0 ? null : string.Join(" ", notes));
    }

    public static bool IsUnclear(FileCategory c) => c is FileCategory.ReviewNeeded or FileCategory.Unknown;

    // ------------------------------------------------------------------ rules

    private enum ContentKind { Image, Video, Audio }

    /// <summary>
    /// The kind of content the first bytes name, else the type by extension. "mp4" (also HEIC and MOV) and
    /// "mp3" (a weak frame-sync test) are not trusted on their own; the extension decides there.
    /// </summary>
    private static ContentKind? KindOf(FileNode file, IReadOnlyList<FileFact> facts)
    {
        var format = facts.FirstOrDefault(f => f.Key == FileFactKey.DetectedFormat)?.Value;
        ContentKind? byHeader = format switch
        {
            "jpeg" or "png" or "gif" or "webp" or "tiff" or "bmp" => ContentKind.Image,
            "avi" or "matroska" => ContentKind.Video,
            "flac" or "ogg" or "wav" => ContentKind.Audio,
            _ => null
        };
        if (byHeader is not null) return byHeader;
        if (format is not null && format is not ("mp4" or "mp3")) return null; // a known other format: no media kind

        return file.FileType switch
        {
            FileType.Image => ContentKind.Image,
            FileType.Video => ContentKind.Video,
            FileType.Audio => ContentKind.Audio,
            _ => null
        };
    }

    /// <summary>
    /// A media category that the file cannot be: Photo or Screenshot for something that is not an image,
    /// Video for a non-video, Audio for a non-audio file. Header bytes and extension beat the model.
    /// </summary>
    public static string? KindMismatch(FileNode file, IReadOnlyList<FileFact> facts, FileCategory category)
    {
        ContentKind? required = category switch
        {
            FileCategory.Photo or FileCategory.Screenshot => ContentKind.Image,
            FileCategory.Video => ContentKind.Video,
            FileCategory.Audio => ContentKind.Audio,
            _ => null
        };
        if (required is null) return null;

        var kind = KindOf(file, facts);
        if (kind == required) return null;
        var what = kind is null ? $"not {Article(required.Value)} file" : $"{Article(kind.Value)} file";
        return $"Category {category} does not fit: first bytes and extension say {what}.";
    }

    private static string Article(ContentKind k) => k switch
    {
        ContentKind.Image => "an image",
        ContentKind.Video => "a video",
        _ => "an audio"
    };

    /// <summary>
    /// Metadata rules that are unambiguous enough to set the category against the model: an image the rules
    /// call a screenshot (screenshot name or screen-sized PNG, no camera data) is no photo, and an image with
    /// a camera make or model is no screenshot. A scan is not forced: a scanned print may well be a photo.
    /// </summary>
    public static (FileCategory Category, string Note)? RuleOverride(IReadOnlyList<FileFact> facts, FileCategory category)
    {
        var origin = facts.FirstOrDefault(f => f.Key == FileFactKey.ImageOrigin)?.Value;
        if (!VerdictCodes.TryParse<ImageOrigin>(origin, out var o)) return null;

        if (o == ImageOrigin.Screenshot && category == FileCategory.Photo)
            return (FileCategory.Screenshot, "Rule image_origin=screenshot (name or screen size, no camera data) sets Screenshot instead of Photo.");

        var hasDevice = facts.Any(f => f.Key == FileFactKey.Device);
        if (o == ImageOrigin.Camera && hasDevice && category == FileCategory.Screenshot)
            return (FileCategory.Photo, "Rule image_origin=camera with a camera make or model sets Photo instead of Screenshot.");

        return null;
    }

    /// <summary>A rule fact says the same as the category: two signals agree.</summary>
    public static bool RuleSupports(IReadOnlyList<FileFact> facts, FileCategory category)
    {
        var origin = facts.FirstOrDefault(f => f.Key == FileFactKey.ImageOrigin)?.Value;
        if (VerdictCodes.TryParse<ImageOrigin>(origin, out var o))
        {
            return (o, category) is (ImageOrigin.Camera, FileCategory.Photo)
                or (ImageOrigin.Screenshot, FileCategory.Screenshot)
                or (ImageOrigin.Scan, FileCategory.DocumentScan);
        }
        return category == FileCategory.Audio && facts.Any(f => f.Key == FileFactKey.AudioKind);
    }

    /// <summary>
    /// The file and the retention of its directory point different ways: an important file in a directory
    /// judged disposable (what <c>in_noise</c> findings are made of), or a trash candidate or temporary file
    /// in a directory to keep (what could lead to a wrong deletion).
    /// </summary>
    public static string? VerdictContradiction(FileClassification answer, DirectoryVerdict? verdict)
    {
        if (verdict is null || verdict.Error is not null || verdict.Retention is null) return null;

        return verdict.Retention switch
        {
            RetentionValue.Entbehrlich when answer.Importance is Importance.High or Importance.Critical =>
                $"Importance {answer.Importance}, but the directory was judged {verdict.Retention.ToCode()}.",
            RetentionValue.Behalten when answer.Category == FileCategory.TrashCandidate =>
                $"Category TrashCandidate, but the directory was judged {verdict.Retention.ToCode()}.",
            RetentionValue.Behalten when answer.KeepHint == KeepHint.Temporary =>
                $"Keep hint temporary, but the directory was judged {verdict.Retention.ToCode()}.",
            _ => null
        };
    }

    // ------------------------------------------------------------------ certainty

    /// <summary>Certainty of a model answer that needed no second call.</summary>
    public static ResultCertainty SingleAnswerCertainty(IReadOnlyList<FileFact> facts, FileClassification answer) =>
        RuleSupports(facts, answer.Category) ? ResultCertainty.Certain : ResultCertainty.Likely;

    /// <summary>A rule result is certain when the rule is sure (program, font, thumbnail cache), otherwise uncertain.</summary>
    public static ResultCertainty RuleResultCertainty(FileClassification rule) =>
        rule.Confidence >= CertainRuleConfidence && !IsUnclear(rule.Category) ? ResultCertainty.Certain : ResultCertainty.Uncertain;

    // ------------------------------------------------------------------ combining two answers

    /// <summary>
    /// Combines the first answer with the second one (more evidence, directory context, never the first
    /// answer). Both are kept unchanged in the check; this returns the result to store.
    /// </summary>
    public static (FileClassification Result, CheckOutcome Outcome) Combine(
        FileNode file, IReadOnlyList<FileFact> facts, FileClassification first, FileClassification second)
    {
        if (second.Error is not null)
        {
            first.Certainty = ResultCertainty.Uncertain;
            return (first, CheckOutcome.Failed);
        }

        var secondFitsKind = KindMismatch(file, facts, second.Category) is null;
        var firstFitsKind = KindMismatch(file, facts, first.Category) is null;

        if (first.Category == second.Category && KeepHintsAgree(first, second) && secondFitsKind)
        {
            second.Confidence = Combined(first, second);
            second.ConfidenceMissing = false;
            second.Certainty = IsUnclear(second.Category) ? ResultCertainty.Review : ResultCertainty.Certain;
            return (second, CheckOutcome.Agreed);
        }

        if (!firstFitsKind && secondFitsKind && !IsUnclear(second.Category))
        {
            // The rule rejected the first answer and the second one fits it: rule and second answer agree.
            second.Certainty = ResultCertainty.Likely;
            return (second, CheckOutcome.Corrected);
        }

        return (Disagreement(first, second), CheckOutcome.Disagreed);
    }

    private static bool KeepHintsAgree(FileClassification a, FileClassification b) =>
        a.KeepHint is null || b.KeepHint is null || a.KeepHint == b.KeepHint;

    /// <summary>Two agreeing answers: 1 - (1 - c1)(1 - c2), a missing confidence counting as 0.5. Always above both.</summary>
    public static double Combined(FileClassification a, FileClassification b)
    {
        var c1 = a.ConfidenceMissing ? UnstatedConfidence : a.Confidence;
        var c2 = b.ConfidenceMissing ? UnstatedConfidence : b.Confidence;
        return Math.Round(1 - (1 - c1) * (1 - c2), 3);
    }

    /// <summary>
    /// The stored result when two answers disagree: ReviewNeeded, the higher importance, the lower confidence,
    /// no target, worth keeping if either answer says so, all sensitive markers. The summary names both
    /// categories only, never model text, so it is safe for sensitive files too.
    /// </summary>
    private static FileClassification Disagreement(FileClassification first, FileClassification second)
    {
        var c1 = first.ConfidenceMissing ? 0 : first.Confidence;
        var c2 = second.ConfidenceMissing ? 0 : second.Confidence;
        KeepHint? keep = first.KeepHint == KeepHint.WorthKeeping || second.KeepHint == KeepHint.WorthKeeping
            ? KeepHint.WorthKeeping
            : first.KeepHint ?? second.KeepHint;

        return new FileClassification
        {
            FileNodeId = second.FileNodeId,
            Category = FileCategory.ReviewNeeded,
            Importance = (Importance)Math.Max((int)first.Importance, (int)second.Importance),
            Confidence = Math.Min(c1, c2),
            Summary = string.Create(CultureInfo.InvariantCulture,
                $"Two looks disagree: {first.Category} first, {second.Category} with more evidence. Both answers are kept in the check."),
            SuggestedTarget = null,
            ModelUsed = second.ModelUsed,
            AnalyzedAt = second.AnalyzedAt,
            RawResponse = second.RawResponse,
            IsAiResult = true,
            SensitiveMarkers = first.SensitiveMarkers | second.SensitiveMarkers,
            KeepHint = keep,
            KeepReason = first.KeepHint == second.KeepHint ? second.KeepReason : (keep is null ? null : "The two answers disagree."),
            Evidence = second.Evidence,
            Certainty = ResultCertainty.Review
        };
    }
}
