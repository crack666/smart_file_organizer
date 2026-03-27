using Dapper;
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
                (file_node_id, overridden_category, overridden_target, note, overridden_at)
            VALUES
                (@FileNodeId, @OverriddenCategory, @OverriddenTarget, @Note, @OverriddenAt)
            ON CONFLICT(file_node_id) DO UPDATE SET
                overridden_category = excluded.overridden_category,
                overridden_target = excluded.overridden_target,
                note = excluded.note,
                overridden_at = excluded.overridden_at
            """;

        await conn.ExecuteAsync(sql, uo);
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
