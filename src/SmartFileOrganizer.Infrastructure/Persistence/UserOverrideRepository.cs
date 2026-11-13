using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

public class UserOverrideRepository : IUserOverrideRepository
{
    private readonly DatabaseContext _db;

    public UserOverrideRepository(DatabaseContext db) => _db = db;

    public async Task UpsertAsync(UserOverride uo, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);

        const string sql = """
            INSERT INTO user_overrides
                (file_node_id, overridden_category, overridden_target, note, overridden_at, origin)
            VALUES
                (@FileNodeId, @OverriddenCategory, @OverriddenTarget, @Note, @OverriddenAt, @Origin)
            ON CONFLICT(file_node_id) DO UPDATE SET
                overridden_category = excluded.overridden_category,
                overridden_target = excluded.overridden_target,
                note = excluded.note,
                overridden_at = excluded.overridden_at,
                origin = excluded.origin
            """;

        // The origin as its code; Dapper would bind the enum as a number.
        await conn.ExecuteAsync(sql, new
        {
            uo.FileNodeId, uo.OverriddenCategory, uo.OverriddenTarget, uo.Note, uo.OverriddenAt,
            Origin = (uo.Origin ?? ResultOrigin.Human).ToCode()
        });
    }

    /// <summary>Adds a correction carried over from the content store, unless the file already has one.</summary>
    public async Task<bool> InsertIfAbsentAsync(UserOverride uo, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var added = await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO user_overrides (file_node_id, overridden_category, overridden_target, note, overridden_at, origin)
            VALUES (@FileNodeId, @OverriddenCategory, @OverriddenTarget, @Note, @OverriddenAt, @Origin)
            ON CONFLICT(file_node_id) DO NOTHING
            """,
            new
            {
                uo.FileNodeId, uo.OverriddenCategory, uo.OverriddenTarget, uo.Note, uo.OverriddenAt,
                Origin = (uo.Origin ?? ResultOrigin.Human).ToCode()
            }, cancellationToken: ct));
        return added > 0;
    }

    public async Task DeleteByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM user_overrides WHERE file_node_id = @fileNodeId",
            new { fileNodeId });
    }

    public async Task<UserOverride?> GetByFileNodeIdAsync(long fileNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserOverride>(
            "SELECT * FROM user_overrides WHERE file_node_id = @fileNodeId",
            new { fileNodeId });
    }

    public async Task<IReadOnlyList<UserOverride>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var result = await conn.QueryAsync<UserOverride>(
            "SELECT * FROM user_overrides ORDER BY overridden_at DESC");
        return result.AsList();
    }
}
