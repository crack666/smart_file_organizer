using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Places;

/// <summary>The short report of a place: what lies there as the findings of its last job see it.</summary>
/// <param name="WorthKeepingNotBackedUp">Worth keeping and in no library: what must not be lost.</param>
/// <param name="BackedUp">Held by a library by content.</param>
/// <param name="Disposable">Noise and temporary files.</param>
public sealed record PlaceReport(FileTally WorthKeepingNotBackedUp, FileTally BackedUp, FileTally Disposable, FileTally Sensitive);

/// <summary>One known place as <c>sfo places</c> shows it.</summary>
/// <param name="Reachable">Whether the root can be reached now; the numbers come from the database either way.</param>
/// <param name="AgeDays">Whole days since the last completed scan.</param>
/// <param name="Report">Null for a library, without a job, or when the findings of the job cannot be computed.</param>
public sealed record PlaceView(Place Place, bool Reachable, int? AgeDays, PlaceReport? Report, string? ReportNote);

/// <summary>The answer of a refresh: the counts and the files that changed.</summary>
public sealed record RefreshReport(RefreshCounts Counts, IReadOnlyList<FileChangeRecord> Changes);

/// <summary>One place a file or content is known at.</summary>
/// <param name="Exists">Whether the path exists now; null when not asked (a path below a root that cannot be reached).</param>
public sealed record WherePlace(string Path, WhereSource Source, bool? Exists, long? JobId, DateTime? SeenAt);

/// <summary>One step of the history of a file.</summary>
public sealed record WhereMove(string From, string? To, DateTime MovedAt, long PlanId, long? ActionId, MoveState State,
    DateTime? ReturnedAt, bool Purged, WhereSource Source);

/// <summary>The answer of <c>sfo where</c>.</summary>
/// <param name="Kind"><c>path</c> or <c>hash</c>.</param>
/// <param name="Hash">The full checksum, given or stored for the path.</param>
/// <param name="Now">Where it lies today, as far as the database knows; the newest first.</param>
/// <param name="History">How it moved, oldest first.</param>
public sealed record WhereReport(string Query, string Kind, string? Hash, IReadOnlyList<WherePlace> Now, IReadOnlyList<WhereMove> History);

/// <summary>
/// The memory of the tool (https://github.com/crack666/smart_file_organizer/issues/86): known places with a short report,
/// the refresh of a place against its previous job, and where a file went. Reads only the database (and whether a path
/// exists); calls no model and reads no file.
/// </summary>
public sealed class PlaceService
{
    private readonly IPlaceRepository _places;
    private readonly FindingService _findings;
    private readonly TimeProvider _time;

    public PlaceService(IPlaceRepository places, FindingService findings, TimeProvider? time = null)
    {
        _places = places;
        _findings = findings;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<PlaceView>> ListAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var result = new List<PlaceView>();
        foreach (var place in await _places.GetAllAsync(ct))
        {
            var (report, note) = await ReportAsync(place, ct);
            result.Add(new PlaceView(place, Reachable(place.RootPath),
                place.LastScanAt is { } at ? (int)Math.Floor((now - at).TotalDays) : null, report, note));
        }
        return result;
    }

    private async Task<(PlaceReport?, string?)> ReportAsync(Place place, CancellationToken ct)
    {
        if (place.Role != ScanRole.Source)
            return (null, "A library has no findings.");
        if (place.LastJobId is not { } jobId)
            return (null, "No job of this place is left.");
        try
        {
            var report = await _findings.ComputeAsync(jobId, ct);
            if (report.Overview is not { } o)
                return (null, "The findings of the last job have no overview.");
            return (new PlaceReport(o.WorthKeepingNotBackedUp, o.BackedUp, o.Disposable, o.Sensitive), null);
        }
        catch (Exception ex) when (ex is FindingException or JobReadException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>The newest completed job of the place of <paramref name="job"/>, other than itself.</summary>
    public Task<long?> PreviousJobAsync(ScanJob job, CancellationToken ct = default) =>
        _places.GetPreviousJobIdAsync(job.RootPath, job.Role, job.Id, ct);

    /// <summary>Compares a new job with the previous one of its place and carries over what still holds.</summary>
    public async Task<RefreshReport> RefreshAsync(long previousJobId, long jobId, CancellationToken ct = default)
    {
        var counts = await _places.RefreshAsync(previousJobId, jobId, ct);
        return new RefreshReport(counts, await _places.GetChangesAsync(jobId, ct));
    }

    /// <summary>Where a file (by path) or a content (by full SHA-256) lies today and how it moved.</summary>
    public async Task<WhereReport> WhereAsync(string query, CancellationToken ct = default)
    {
        var trimmed = query.Trim();
        if (IsHash(trimmed))
            return await WhereHashAsync(trimmed.ToLowerInvariant(), ct);

        var path = Path.GetFullPath(trimmed);
        var history = new List<WhereMove>();
        var moves = await _places.GetMovesFromAsync(path, ct);
        history.AddRange(moves.Select(m => Move(m, WhereSource.Journal)));
        // What arrived at the path, so the question about a library file also tells where it came from.
        var arrived = await _places.GetMovesToAsync(path, ct);
        history.AddRange(arrived.Where(m => moves.All(x => x.Id != m.Id)).Select(m => Move(m, WhereSource.Journal)));
        foreach (var n in await _places.GetNoticeEntriesAsync(path, ct))
        {
            if (history.Any(h => SamePath(h.From, n.SourcePath) && h.PlanId == n.PlanId && h.State == MoveState.Moved)) continue;
            history.Add(new WhereMove(n.SourcePath, n.TargetPath, n.MovedAt, n.PlanId, null, MoveState.Moved, null, false, WhereSource.Notice));
        }

        var now = new List<WherePlace>();
        // A move that has not come back: the file lies at its target.
        foreach (var m in history.Where(h => h.State == MoveState.Moved && h.To is not null && !arrived.Any(a => SamePath(a.TargetPath, h.To)))
                                 .OrderByDescending(h => h.MovedAt))
            Add(now, new WherePlace(m.To!, m.Source, Exists(m.To!), null, m.MovedAt));
        // Not moved away, or came back: where it is or was seen.
        if (!history.Any(h => SamePath(h.From, path) && h.State == MoveState.Moved && !arrived.Any(a => SamePath(a.TargetPath, h.To))))
        {
            foreach (var seen in await _places.GetSeenAsync(path, ct))
                Add(now, new WherePlace(seen.FullPath, WhereSource.Scan, Exists(seen.FullPath), seen.JobId, seen.ScannedAt));
            if (now.Count == 0 && File.Exists(path))
                Add(now, new WherePlace(path, WhereSource.Scan, true, null, null));
        }

        var hash = await _places.GetHashOfPathAsync(path, ct) ?? moves.Concat(arrived).Select(m => m.Hash).LastOrDefault(h => h is not null);
        return new WhereReport(query, "path", hash, now, history.OrderBy(h => h.MovedAt).ToList());
    }

    private async Task<WhereReport> WhereHashAsync(string hash, CancellationToken ct)
    {
        var moves = await _places.GetMovesByHashAsync(hash, ct);
        var history = moves.Select(m => Move(m, WhereSource.Journal)).OrderBy(h => h.MovedAt).ToList();
        var now = new List<WherePlace>();
        foreach (var m in history.Where(h => h.State == MoveState.Moved && h.To is not null).OrderByDescending(h => h.MovedAt))
            Add(now, new WherePlace(m.To!, WhereSource.Journal, Exists(m.To!), null, m.MovedAt));
        foreach (var p in await _places.GetPathsByHashAsync(hash, ct))
        {
            // A path the content left through a plan that did not bring it back is history, not a place of today.
            if (history.Any(h => SamePath(h.From, p) && h.State == MoveState.Moved)) continue;
            Add(now, new WherePlace(p, WhereSource.Checksum, Exists(p), null, null));
        }
        return new WhereReport(hash, "hash", hash, now, history);
    }

    private static WhereMove Move(MoveRecord m, WhereSource source) =>
        new(m.SourcePath, m.TargetPath, m.MovedAt, m.PlanId, m.ActionId, m.State, m.ReturnedAt, m.PurgedAt is not null, source);

    private static void Add(List<WherePlace> list, WherePlace place)
    {
        if (list.All(p => !SamePath(p.Path, place.Path)))
            list.Add(place);
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool Reachable(string root)
    {
        try { return Directory.Exists(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Exists(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
