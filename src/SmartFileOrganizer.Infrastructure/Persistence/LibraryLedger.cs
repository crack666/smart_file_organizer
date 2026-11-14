using System.Data.Common;
using Dapper;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// Writes the holding of a library forward after a plan run (https://github.com/crack666/smart_file_organizer/issues/86),
/// in the transaction of the journal step: a checked copy becomes a file of the library job with its checksum, and the
/// source file counts as <c>backed_up</c> in the newest comparison of the source against that library, without a new
/// scan of the library. What it changed is kept in <c>library_entries</c>, so taking the copy back takes the entry back.
/// </summary>
internal static class LibraryLedger
{
    private static readonly string Collate = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>After a finished <c>copy</c> or <c>present</c> step: the file at the target is part of the library.</summary>
    public static async Task RecordAsync(DbConnection conn, DbTransaction tx, PlanStep step, CancellationToken ct)
    {
        if (step.TargetPath is not { Length: > 0 } target || step.Hash is not { Length: > 0 } hash)
            return;
        var plan = await conn.QuerySingleOrDefaultAsync<PlanRow>(new CommandDefinition(
            "SELECT job_id, libraries FROM plans WHERE id = @PlanId", new { step.PlanId }, tx, cancellationToken: ct));
        if (plan is null)
            return;
        var library = (await conn.QueryAsync<LibraryRow>(new CommandDefinition(
                "SELECT id, root_path FROM scan_jobs WHERE role = 'library' AND id IN (SELECT value FROM json_each(@Libraries))",
                new { plan.Libraries }, tx, cancellationToken: ct)))
            .Where(l => PlanChecks.IsAtOrBelow(target, l.RootPath))
            .OrderByDescending(l => l.RootPath.Length).ThenByDescending(l => l.Id)
            .FirstOrDefault();
        var info = new FileInfo(target);
        if (library is null || !info.Exists)
            return;

        var now = DateTime.UtcNow;
        var source = await conn.QuerySingleOrDefaultAsync<SourceRow>(new CommandDefinition(
            $"""
            SELECT id, extension, file_type, sensitive_markers, sensitive_rule, sensitive_rules_version FROM file_nodes
            WHERE job_id = @JobId AND full_path = @path{Collate} ORDER BY id LIMIT 1
            """, new { plan.JobId, path = step.SourcePath }, tx, cancellationToken: ct));

        // The file at the target, as a scan of the library would list it; a row the scan already had is used as it is.
        var nodeId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            $"SELECT id FROM file_nodes WHERE job_id = @lib AND full_path = @target{Collate} ORDER BY id LIMIT 1",
            new { lib = library.Id, target }, tx, cancellationToken: ct));
        var added = nodeId is null;
        if (nodeId is null)
        {
            var relative = Path.GetRelativePath(library.RootPath, target);
            var parent = Path.GetDirectoryName(target)!;
            nodeId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO file_nodes (job_id, full_path, name, parent_path, root_path, relative_path, relative_dir, depth, size,
                                        last_write_time, extension, file_type, status, scanned_at,
                                        sensitive_markers, sensitive_rule, sensitive_rules_version)
                VALUES (@lib, @target, @name, @parent, @root, @relative, @relativeDir, @depth, @size,
                        @lastWrite, @extension, @fileType, 0, @now, @markers, @rule, @rulesVersion);
                SELECT last_insert_rowid();
                """,
                new
                {
                    lib = library.Id, target, name = Path.GetFileName(target), parent, root = library.RootPath, relative,
                    relativeDir = Path.GetRelativePath(library.RootPath, parent),
                    depth = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length,
                    size = info.Length, lastWrite = info.LastWriteTimeUtc,
                    extension = source?.Extension ?? Path.GetExtension(target).ToLowerInvariant(),
                    fileType = source?.FileType ?? 0, now,
                    markers = source?.SensitiveMarkers, rule = source?.SensitiveRule, rulesVersion = source?.SensitiveRulesVersion
                }, tx, cancellationToken: ct));
        }

        // The checksum of the copy is known; a later comparison or duplicate run reads nothing for it.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO content_fingerprints (full_path, size, last_write_ticks, sample_hash, full_hash, computed_at, full_hash_at)
            VALUES (@target, @size, @ticks, NULL, @hash, @now, @now)
            ON CONFLICT(full_path) DO UPDATE SET
                sample_hash = CASE WHEN size = excluded.size AND last_write_ticks = excluded.last_write_ticks THEN sample_hash END,
                size = excluded.size,
                last_write_ticks = excluded.last_write_ticks,
                full_hash = excluded.full_hash,
                computed_at = excluded.computed_at,
                full_hash_at = excluded.full_hash_at
            """,
            new { target, size = info.Length, ticks = info.LastWriteTimeUtc.Ticks, hash, now }, tx, cancellationToken: ct));

        // The source file is backed up in the newest comparison against this library.
        var comparisonId = source is null ? null : await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM comparisons WHERE source_job_id = @JobId AND against_job_id = @lib ORDER BY id DESC LIMIT 1",
            new { plan.JobId, lib = library.Id }, tx, cancellationToken: ct));
        ComparisonRow? previous = null;
        if (comparisonId is not null)
        {
            var args = new { c = comparisonId, f = source!.Id, node = nodeId, hash, now };
            previous = await conn.QuerySingleOrDefaultAsync<ComparisonRow>(new CommandDefinition(
                """
                SELECT state, match_file_node_id, match_count, error, match_hash, match_verified_at FROM comparison_files
                WHERE comparison_id = @c AND file_node_id = @f
                """, args, tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(previous is null
                ? """
                  INSERT INTO comparison_files (comparison_id, file_node_id, state, match_file_node_id, match_count, match_hash, match_verified_at)
                  VALUES (@c, @f, 'backed_up', @node, 1, @hash, @now)
                  """
                : """
                  UPDATE comparison_files SET state = 'backed_up', match_file_node_id = @node,
                      match_count = CASE WHEN match_count < 1 THEN 1 ELSE match_count END,
                      error = NULL, match_hash = @hash, match_verified_at = @now
                  WHERE comparison_id = @c AND file_node_id = @f
                  """, args, tx, cancellationToken: ct));
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO library_entries (plan_id, action_id, step_id, library_job_id, target_path, file_node_id, node_added,
                comparison_id, source_file_node_id, had_row, prev_state, prev_match_file_node_id, prev_match_count, prev_error,
                prev_match_hash, prev_match_verified_at, recorded_at)
            VALUES (@PlanId, @ActionId, @StepId, @lib, @target, @node, @added, @c, @f, @hadRow, @State, @MatchFileNodeId,
                COALESCE(@MatchCount, 0), @Error, @MatchHash, @MatchVerifiedAt, @now)
            """,
            new
            {
                step.PlanId, step.ActionId, StepId = step.Id, lib = library.Id, target, node = nodeId, added = added ? 1 : 0,
                c = comparisonId, f = comparisonId is null ? null : source?.Id, hadRow = previous is null ? 0 : 1,
                previous?.State, previous?.MatchFileNodeId, previous?.MatchCount, previous?.Error, previous?.MatchHash,
                previous?.MatchVerifiedAt, now
            }, tx, cancellationToken: ct));
    }

    /// <summary>After a finished <c>uncopy</c> step: the copy is gone, so what <see cref="RecordAsync"/> wrote for it is taken back.</summary>
    public static async Task RevertAsync(DbConnection conn, DbTransaction tx, PlanStep step, CancellationToken ct)
    {
        if (step.TargetPath is not { Length: > 0 } target)
            return;
        var entries = (await conn.QueryAsync<EntryRow>(new CommandDefinition(
            """
            SELECT id, target_path, file_node_id, node_added, comparison_id, source_file_node_id, had_row, prev_state, prev_match_file_node_id,
                   prev_match_count, prev_error, prev_match_hash, prev_match_verified_at
            FROM library_entries WHERE plan_id = @PlanId AND action_id = @ActionId AND undone_at IS NULL ORDER BY id DESC
            """, new { step.PlanId, step.ActionId }, tx, cancellationToken: ct)))
            .Where(e => string.Equals(e.TargetPath, target, PathComparison))
            .ToList();
        var now = DateTime.UtcNow;
        foreach (var e in entries)
        {
            if (e.ComparisonId is not null && e.SourceFileNodeId is not null)
            {
                await conn.ExecuteAsync(new CommandDefinition(e.HadRow != 0
                    ? """
                      UPDATE comparison_files SET state = @PrevState, match_file_node_id = @PrevMatchFileNodeId,
                          match_count = @PrevMatchCount, error = @PrevError, match_hash = @PrevMatchHash,
                          match_verified_at = @PrevMatchVerifiedAt
                      WHERE comparison_id = @ComparisonId AND file_node_id = @SourceFileNodeId
                      """
                    : "DELETE FROM comparison_files WHERE comparison_id = @ComparisonId AND file_node_id = @SourceFileNodeId",
                    e, tx, cancellationToken: ct));
            }

            var drop = e.NodeAdded != 0 && e.FileNodeId is not null && await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(*) FROM library_entries WHERE file_node_id = @FileNodeId AND id <> @Id AND undone_at IS NULL",
                e, tx, cancellationToken: ct)) == 0;
            await conn.ExecuteAsync(new CommandDefinition(
                $"UPDATE library_entries SET undone_at = @now{(drop ? ", file_node_id = NULL" : string.Empty)} WHERE id = @Id",
                new { e.Id, now }, tx, cancellationToken: ct));
            if (!drop)
                continue;

            // A comparison made after the run that found the copy loses that match; the copy is gone.
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE comparison_files SET state = 'new', match_file_node_id = NULL, match_count = 0, match_hash = NULL,
                    match_verified_at = NULL
                WHERE match_file_node_id = @FileNodeId;
                UPDATE library_entries SET file_node_id = NULL WHERE file_node_id = @FileNodeId;
                DELETE FROM file_nodes WHERE id = @FileNodeId;
                DELETE FROM content_fingerprints WHERE full_path = @target AND full_hash = @hash;
                """, new { e.FileNodeId, target, hash = step.Hash }, tx, cancellationToken: ct));
        }
    }

    private sealed class PlanRow
    {
        public long JobId { get; set; }
        public string Libraries { get; set; } = "[]";
    }

    private sealed class LibraryRow
    {
        public long Id { get; set; }
        public string RootPath { get; set; } = string.Empty;
    }

    private sealed class SourceRow
    {
        public long Id { get; set; }
        public string Extension { get; set; } = string.Empty;
        public long FileType { get; set; }
        public string? SensitiveMarkers { get; set; }
        public string? SensitiveRule { get; set; }
        public long? SensitiveRulesVersion { get; set; }
    }

    private sealed class ComparisonRow
    {
        public string? State { get; set; }
        public long? MatchFileNodeId { get; set; }
        public long? MatchCount { get; set; }
        public string? Error { get; set; }
        public string? MatchHash { get; set; }
        public string? MatchVerifiedAt { get; set; }
    }

    private sealed class EntryRow
    {
        public long Id { get; set; }
        public string TargetPath { get; set; } = string.Empty;
        public long? FileNodeId { get; set; }
        public long NodeAdded { get; set; }
        public long? ComparisonId { get; set; }
        public long? SourceFileNodeId { get; set; }
        public long HadRow { get; set; }
        public string? PrevState { get; set; }
        public long? PrevMatchFileNodeId { get; set; }
        public long PrevMatchCount { get; set; }
        public string? PrevError { get; set; }
        public string? PrevMatchHash { get; set; }
        public string? PrevMatchVerifiedAt { get; set; }
    }
}
