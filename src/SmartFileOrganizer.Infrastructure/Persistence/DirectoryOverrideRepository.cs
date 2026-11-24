using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>Stores corrections of directory verdicts in <c>directory_overrides</c>, enum values as their fixed codes.</summary>
public sealed class DirectoryOverrideRepository : IDirectoryOverrideRepository
{
    private readonly DatabaseContext _db;

    public DirectoryOverrideRepository(DatabaseContext db) => _db = db;

    public async Task UpsertAsync(DirectoryOverride o, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO directory_overrides (directory_node_id, verdict, retention, note, overridden_at)
            VALUES (@DirectoryNodeId, @Verdict, @Retention, @Note, @OverriddenAt)
            ON CONFLICT(directory_node_id) DO UPDATE SET
                verdict       = excluded.verdict,
                retention     = excluded.retention,
                note          = excluded.note,
                overridden_at = excluded.overridden_at
            """,
            new { o.DirectoryNodeId, Verdict = o.Verdict.ToCode(), Retention = o.Retention.ToCode(), o.Note, o.OverriddenAt },
            cancellationToken: ct));
    }

    public async Task<bool> DeleteAsync(long directoryNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM directory_overrides WHERE directory_node_id = @directoryNodeId",
            new { directoryNodeId }, cancellationToken: ct)) > 0;
    }

    public async Task<int> ApplyToFilesAsync(long directoryNodeId, bool handledAsWhole, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE file_nodes SET status = @to
            WHERE status = @from AND analysis_skip IS NULL
              AND id NOT IN (SELECT file_node_id FROM ai_results)
              AND job_id = (SELECT job_id FROM directory_nodes WHERE id = @directoryNodeId)
              AND parent_path = (SELECT full_path FROM directory_nodes WHERE id = @directoryNodeId)
            """,
            new
            {
                directoryNodeId,
                from = (int)(handledAsWhole ? FileNodeStatus.Discovered : FileNodeStatus.Skipped),
                to = (int)(handledAsWhole ? FileNodeStatus.Skipped : FileNodeStatus.Discovered)
            }, cancellationToken: ct));
    }
}
