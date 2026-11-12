using Microsoft.Data.Sqlite;

namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// One numbered schema step. Steps are discovered by reflection from this assembly,
/// so adding a step means adding one file — there is no central list to edit.
/// </summary>
/// <remarks>
/// Versions must form the sequence 1..N without gaps or duplicates; the migrator refuses
/// to run otherwise. Each step runs inside its own transaction together with the
/// <c>db_version</c> row that records it.
/// </remarks>
public interface ISchemaMigration
{
    /// <summary>Step number, starting at 1.</summary>
    int Version { get; }

    /// <summary>Short human-readable description, used in logs.</summary>
    string Description { get; }

    Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct);
}

/// <summary>
/// Convenience base for steps that are plain SQL. Use <see cref="ISchemaMigration"/> directly
/// when a step needs code (for example a data backfill).
/// </summary>
public abstract class SqlSchemaMigration : ISchemaMigration
{
    public abstract int Version { get; }
    public abstract string Description { get; }

    /// <summary>DDL/DML to execute. May contain several statements. Must not contain PRAGMAs
    /// that are illegal inside a transaction (for example <c>journal_mode</c>).</summary>
    protected abstract string Sql { get; }

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = Sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
