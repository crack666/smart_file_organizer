using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Media;

/// <summary>
/// Joins the kind of a video the model chose with what the rules read (https://github.com/crack666/smart_file_organizer/issues/103).
/// A known kind of the rules that is at least likely stays, whatever the model says; then the model's other answer is kept
/// as <c>video_kind_disputed</c>. Where the rules were unsure or found nothing, the model's kind is stored (source
/// <c>model</c>, certainty <c>likely</c>) and an unsure kind of the rules it contradicts is kept as the disputed one.
/// </summary>
public static class VideoKindMerge
{
    public static IReadOnlyList<FileFact> Merge(IReadOnlyList<FileFact> facts, IReadOnlyList<FileFact> described)
    {
        var answer = described.FirstOrDefault(f => f.Key == FileFactKey.VideoKind && f.Source == FactSource.Model);
        if (answer is null) return facts;

        var rule = facts.FirstOrDefault(f => f.Key == FileFactKey.VideoKind);
        var known = rule is not null && rule.Value != VideoKind.Unknown.ToCode();
        var list = facts.Where(f => f.Key is not (FileFactKey.VideoKind or FileFactKey.VideoKindDisputed)).ToList();
        if (known && rule!.Certainty is ResultCertainty.Certain or ResultCertainty.Likely)
        {
            list.Add(rule);
            if (rule.Value != answer.Value)
                list.Add(new FileFact(FileFactKey.VideoKindDisputed, answer.Value, FactSource.Model));
        }
        else
        {
            list.Add(answer);
            if (known && rule!.Value != answer.Value)
                list.Add(new FileFact(FileFactKey.VideoKindDisputed, rule.Value, rule.Source, rule.Certainty));
        }
        return list;
    }
}
