using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Assessments by content instead of by path (https://github.com/crack666/smart_file_organizer/issues/38). The same
/// content on a second old disk, under another name or in a new scan is judged once: before a model call the
/// classification asks here for an assessment of the same content (full SHA-256 and size) made with the same model and
/// prompt version, and takes it over as <see cref="ResultOrigin.Reused"/>.
/// </summary>
/// <remarks>
/// <para>Only what belongs to the content is kept and taken over: category, importance, confidence, certainty, the
/// one-sentence summary, the keep hint, the markers the model found and the description of stage 1. What depends on
/// where a file lies is never taken over: folder verdict, findings, target and decision are made anew in every job, and
/// the rules of the new place (name markers, archive content) run again on the taken-over result.</para>
/// <para>A person's correction belongs to the content too: <see cref="RecordCorrectionAsync"/> keeps it, and every file
/// with the same content gets it as a <c>user_overrides</c> row with origin <c>reused</c>, whatever model judges it.</para>
/// <para>The content key comes from <c>content_fingerprints</c> (shared with the library comparison, valid while size and
/// modification time match) or is computed and stored there.</para>
/// </remarks>
public sealed class ContentStoreService
{
    private readonly IContentStoreRepository _store;
    private readonly IContentComparisonRepository _fingerprints;
    private readonly IContentHasher _hasher;
    private readonly IUserOverrideRepository? _overrides;
    private readonly IFileRepository? _files;
    private readonly ILogger _logger;

    public ContentStoreService(
        IContentStoreRepository store,
        IContentComparisonRepository fingerprints,
        IContentHasher hasher,
        IUserOverrideRepository? overrides = null,
        IFileRepository? files = null,
        ILogger<ContentStoreService>? logger = null)
    {
        _store = store;
        _fingerprints = fingerprints;
        _hasher = hasher;
        _overrides = overrides;
        _files = files;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// The content key of the file as it is on disk now, or null when it cannot be read (then nothing is taken over
    /// and nothing is kept). Reuses a stored full checksum while size and modification time match.
    /// </summary>
    public async Task<ContentKey?> KeyOfAsync(FileNode file, CancellationToken ct)
    {
        var stat = _hasher.Stat(file.FullPath);
        if (!stat.Exists) return null;
        try
        {
            var stored = (await _fingerprints.GetFingerprintsAsync([file.FullPath], ct)).GetValueOrDefault(file.FullPath);
            var same = stored is not null && stored.Size == stat.Size && stored.LastWriteTicks == stat.LastWriteTicks;
            if (same && stored!.FullHash is { } known)
                return new ContentKey(known, stat.Size);

            var full = await _hasher.ComputeFullAsync(file.FullPath, stat.Size, ct);
            var now = DateTime.UtcNow;
            // The sample checksum stays when it belongs to the same state of the file.
            await _fingerprints.UpsertFingerprintsAsync(
                [new ContentFingerprint(file.FullPath, stat.Size, stat.LastWriteTicks, same ? stored!.SampleHash : null, full, now, now)], ct);
            return new ContentKey(full, stat.Size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("No content key for file {FileId}: {Error}", file.Id, ex.Message);
            return null;
        }
    }

    /// <summary>An assessment of this content by the same model and prompt version, asked the same way, or null.</summary>
    public Task<ContentAssessment?> FindAsync(ContentKey key, AssessmentIdentity identity, bool keepHintAsked, CancellationToken ct) =>
        _store.FindAsync(key, identity, keepHintAsked, ct);

    /// <summary>
    /// The result a file gets from an entry: what belongs to the content, no target, origin <see cref="ResultOrigin.Reused"/>,
    /// no raw answer (the model was not asked for this file). Counts the use.
    /// </summary>
    public async Task<FileClassification> TakeOverAsync(ContentAssessment entry, FileNode file, bool keepHintAsked, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _store.MarkUsedAsync(entry.Id, now, ct);
        return new FileClassification
        {
            FileNodeId = file.Id,
            Category = entry.Category,
            Importance = entry.Importance,
            Confidence = entry.Confidence,
            Certainty = entry.Certainty,
            Summary = entry.Summary,
            KeepHint = keepHintAsked ? entry.KeepHint : null,
            KeepReason = keepHintAsked ? entry.KeepReason : null,
            SensitiveMarkers = entry.SensitiveMarkers,
            ModelUsed = entry.Identity.Model,
            AnalyzedAt = now,
            IsAiResult = true,
            Origin = ResultOrigin.Reused,
            DescribedFacts = entry.DescribedFacts.ToList()
        };
    }

    /// <summary>
    /// Whether a result may be kept for its content: a model answer without error that a second call with the folder
    /// as context did not shape (that call sees verdict and neighbours, which belong to the place), made with
    /// <paramref name="identity"/>, for a file no rule marked (such a file is never described, so the entry would lack
    /// the description another copy needs).
    /// </summary>
    public static bool MayKeep(FileNode file, FileClassification result, ResultCheck? check, AssessmentIdentity identity) =>
        result.Origin == ResultOrigin.Model
        && result.Error is null
        && string.Equals(result.ModelUsed, identity.Model, StringComparison.Ordinal)
        && (check is null || check.Outcome == CheckOutcome.RuleWins)
        && SensitiveOutputGuard.GetMarkers(file) == SensitiveMarker.None;

    /// <summary>Keeps what the model said about the content; the description only for content the model did not mark.</summary>
    public Task KeepAsync(ContentKey key, AssessmentIdentity identity, bool keepHintAsked, FileClassification result, CancellationToken ct) =>
        _store.SaveAsync(new ContentAssessment
        {
            Key = key,
            Identity = identity,
            KeepHintAsked = keepHintAsked,
            Category = result.Category,
            Importance = result.Importance,
            Confidence = result.Confidence,
            Certainty = result.Certainty,
            Summary = result.Summary,
            KeepHint = result.KeepHint,
            KeepReason = result.KeepReason,
            SensitiveMarkers = result.SensitiveMarkers,
            DescribedFacts = result.SensitiveMarkers == SensitiveMarker.None
                ? result.DescribedFacts.Where(f => f.Key.IsDescription()).ToList()
                : [],
            CreatedAt = DateTime.UtcNow
        }, ct);

    /// <summary>
    /// Gives the file the correction a person made for its content, as a <c>user_overrides</c> row with origin
    /// <c>reused</c>; a file with a correction of its own keeps it. Returns the correction carried over, or null.
    /// </summary>
    public async Task<UserOverride?> CarryCorrectionAsync(FileNode file, ContentKey key, CancellationToken ct)
    {
        if (_overrides is null || file.Override is not null) return null;
        var correction = await _store.FindCorrectionAsync(key, ct);
        if (correction is null) return null;

        var carried = new UserOverride
        {
            FileNodeId = file.Id,
            OverriddenCategory = correction.Category,
            Note = correction.Note,
            OverriddenAt = DateTime.UtcNow,
            Origin = ResultOrigin.Reused
        };
        if (!await _overrides.InsertIfAbsentAsync(carried, ct)) return null;
        file.Override = carried;
        return carried;
    }

    /// <summary>
    /// Keeps a person's correction for the content of the file, so it follows the content into every job. The target of
    /// the correction is left out: it belongs to the place. Nothing happens when the file cannot be read.
    /// </summary>
    public async Task RecordCorrectionAsync(long fileNodeId, FileCategory category, string? note, CancellationToken ct)
    {
        if (await KeyOfFileAsync(fileNodeId, ct) is { } key)
            await _store.SaveCorrectionAsync(new ContentCorrection(key, category, note, DateTime.UtcNow), ct);
    }

    /// <summary>A person withdrew the correction of the file: it no longer follows its content either.</summary>
    public async Task WithdrawCorrectionAsync(long fileNodeId, CancellationToken ct)
    {
        if (await KeyOfFileAsync(fileNodeId, ct) is { } key)
            await _store.DeleteCorrectionAsync(key, ct);
    }

    public Task<ContentStoreStats> GetStatsAsync(AssessmentIdentity? current, CancellationToken ct) => _store.GetStatsAsync(current, ct);

    public Task<(long Entries, long Corrections)> ClearAsync(AssessmentIdentity? current, bool onlyStale, bool corrections, CancellationToken ct) =>
        _store.ClearAsync(current, onlyStale, corrections, ct);

    private async Task<ContentKey?> KeyOfFileAsync(long fileNodeId, CancellationToken ct)
    {
        if (_files is null) return null;
        var file = await _files.GetByIdAsync(fileNodeId, ct);
        return file is null ? null : await KeyOfAsync(file, ct);
    }
}
