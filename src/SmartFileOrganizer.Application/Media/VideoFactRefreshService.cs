using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Media;

/// <summary>What <c>sfo facts refresh</c> did for one job.</summary>
/// <param name="Videos">Video files of the job.</param>
/// <param name="Read">Videos whose file could still be read.</param>
/// <param name="Missing">Videos whose file is gone or unreadable; their facts stay as they were.</param>
/// <param name="Kinds">Videos per kind after the refresh, by code.</param>
/// <param name="ModelKindsKept">Videos whose kind still comes from an earlier model answer, because the file leaves it open.</param>
/// <param name="Disputed">Videos where an earlier model answer contradicts a sure kind of the file.</param>
/// <param name="Certainty">Per kind code, the videos per result certainty code of their kind (<c>certain</c>, <c>likely</c>, <c>uncertain</c>).</param>
/// <param name="Files">With evidence asked for: each read video with its kind and what the rules looked at; otherwise null.</param>
public sealed record VideoFactRefreshResult(long JobId, int Videos, int Read, int Missing,
    IReadOnlyDictionary<string, int> Kinds, int ModelKindsKept, int Disputed,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>? Certainty = null,
    IReadOnlyList<VideoFactRefreshFile>? Files = null);

/// <summary>One video of <c>sfo facts refresh --verbose</c>: the stored kind and the evidence of the rules.</summary>
/// <param name="Kind">The stored video kind code (after joining an earlier model answer).</param>
/// <param name="Source">Where the stored kind comes from, a fact source code.</param>
/// <param name="RuleKind">What the rules alone read, when an earlier model answer is stored instead.</param>
/// <param name="Reason">The rules' reason; null for a file marked sensitive.</param>
/// <param name="Evidence">What the rules looked at, also what they ignored; values left out for a file marked sensitive.</param>
public sealed record VideoFactRefreshFile(long FileId, string RelativePath, string Kind, string Source, string? Certainty,
    string? Device, string? DeviceCertainty, string? RuleKind, string? Reason, IReadOnlyList<VideoEvidence> Evidence);

/// <summary>
/// Reads the video facts of a stored job again from container, name and companion files, without a model
/// (https://github.com/crack666/smart_file_organizer/issues/103). Jobs analysed before the video facts existed get them,
/// and a job keeps up with better rules. Only the video facts (<c>video_kind</c> to <c>video_kind_disputed</c>) are
/// replaced; every other fact and the analysis itself stay. A kind the model gave earlier stays where the file still
/// leaves the kind open, and becomes the disputed kind where the file now settles it otherwise.
/// </summary>
public sealed class VideoFactRefreshService
{
    private static readonly FileFactKey[] VideoKeys = Enum.GetValues<FileFactKey>().Where(FileFactKeys.IsVideoOrigin).ToArray();

    private readonly IFileRepository _files;
    private readonly IFileFactRepository _facts;
    private readonly IVideoOriginReader _reader;

    public VideoFactRefreshService(IFileRepository files, IFileFactRepository facts, IVideoOriginReader reader)
    {
        _files = files;
        _facts = facts;
        _reader = reader;
    }

    /// <param name="withEvidence">Also return each video with the evidence of the rules (<c>--verbose</c>).</param>
    public async Task<VideoFactRefreshResult> RefreshAsync(long jobId, bool withEvidence = false, CancellationToken ct = default)
    {
        var videos = await _files.GetByTypeAsync(jobId, FileType.Video, ct);
        var stored = await _facts.GetByJobAsync(jobId, ct);
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var certainty = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var files = withEvidence ? new List<VideoFactRefreshFile>() : null;
        int read = 0, missing = 0, modelKept = 0, disputed = 0;

        foreach (var file in videos)
        {
            ct.ThrowIfCancellationRequested();
            var reading = File.Exists(file.FullPath) ? _reader.Read(file) : null;
            if (reading is null)
            {
                missing++;
                continue;
            }
            read++;

            var facts = VideoKindMerge.Merge(reading.Facts, EarlierModelAnswer(stored.GetValueOrDefault(file.Id)));
            var marked = SensitiveOutputGuard.GetMarkers(file) != SensitiveMarker.None;
            if (marked)
                facts = facts.Where(f => !f.Key.IsFreeText()).ToList();
            await _facts.ReplaceKeysAsync(file.Id, VideoKeys, facts, ct);

            var kind = facts.FirstOrDefault(f => f.Key == FileFactKey.VideoKind);
            var code = kind?.Value ?? VideoKind.Unknown.ToCode();
            kinds[code] = kinds.GetValueOrDefault(code) + 1;
            var sure = (kind?.Certainty ?? ResultCertainty.Uncertain).ToCode();
            if (!certainty.TryGetValue(code, out var byCertainty)) certainty[code] = byCertainty = new Dictionary<string, int>(StringComparer.Ordinal);
            byCertainty[sure] = byCertainty.GetValueOrDefault(sure) + 1;
            if (kind?.Source == FactSource.Model) modelKept++;
            if (facts.Any(f => f.Key == FileFactKey.VideoKindDisputed)) disputed++;

            if (files is not null)
            {
                var origin = reading.Origin;
                var device = facts.FirstOrDefault(f => f.Key == FileFactKey.RecordingDevice);
                files.Add(new VideoFactRefreshFile(file.Id, file.RelativePath, code, (kind?.Source ?? FactSource.Rule).ToCode(), kind?.Certainty?.ToCode(),
                    device?.Value, device?.Certainty?.ToCode(),
                    kind?.Source == FactSource.Model ? origin.Kind.ToCode() : null,
                    marked ? null : origin.Reason,
                    (origin.Evidence ?? []).Select(e => marked ? e with { Value = null } : e).ToList()));
            }
        }

        var order = new[] { ResultCertainty.Certain, ResultCertainty.Likely, ResultCertainty.Uncertain }.Select(c => c.ToCode()).ToList();
        return new VideoFactRefreshResult(jobId, videos.Count, read, missing,
            kinds.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value), modelKept, disputed,
            certainty.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key,
                k => (IReadOnlyDictionary<string, int>)k.Value.OrderBy(c => order.IndexOf(c.Key)).ToDictionary(c => c.Key, c => c.Value)),
            files);
    }

    /// <summary>The kind the model named for the file before, as stored: the kind itself or the disputed one.</summary>
    private static IReadOnlyList<FileFact> EarlierModelAnswer(IReadOnlyList<FileFact>? facts)
    {
        var answer = facts?.FirstOrDefault(f => f.Key is FileFactKey.VideoKind or FileFactKey.VideoKindDisputed && f.Source == FactSource.Model);
        return answer is null ? [] : [new FileFact(FileFactKey.VideoKind, answer.Value, FactSource.Model, ResultCertainty.Likely)];
    }
}
