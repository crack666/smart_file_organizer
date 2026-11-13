using System.Globalization;
using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Brings a SQLite database up to the newest schema version.
/// </summary>
/// <remarks>
/// <para>The current version is <c>MAX(version)</c> in <c>db_version</c> (one row per applied step).</para>
/// <para>A database created before numbered migrations has the tables of step 1 but no
/// <c>db_version</c> row. It is recorded as version 1 without re-running step 1.</para>
/// <para>Each pending step runs in its own transaction together with its <c>db_version</c> row,
/// so a failing step leaves the database at the previous version.</para>
/// </remarks>
public sealed class SchemaMigrator
{
    private const string VersionTableDdl = """
        CREATE TABLE IF NOT EXISTS db_version (
            id      INTEGER PRIMARY KEY,
            version INTEGER NOT NULL,
            applied_at TEXT NOT NULL
        );
        """;

    private readonly IReadOnlyList<ISchemaMigration> _migrations;
    private readonly ILogger _logger;

    public SchemaMigrator(IEnumerable<ISchemaMigration> migrations, ILogger logger)
    {
        _migrations = Validate(migrations);
        _logger = logger;
    }

    /// <summary>All steps shipped in this assembly, ordered by version.</summary>
    public static IReadOnlyList<ISchemaMigration> BuiltIn { get; } = Discover(typeof(SchemaMigrator).Assembly);

    public int LatestVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    public IReadOnlyList<ISchemaMigration> Migrations => _migrations;

    /// <summary>Applies all pending steps. Returns the version the database is at afterwards.</summary>
    public async Task<int> MigrateAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        await connection.ExecuteAsync(new CommandDefinition(VersionTableDdl, cancellationToken: ct));

        var current = await GetCurrentVersionAsync(connection, ct);

        if (current == 0 && await HasLegacySchemaAsync(connection, ct))
        {
            _logger.LogInformation("Existing database without version record found; recording it as schema version 1");
            await RecordVersionAsync(connection, transaction: null, 1, ct);
            current = 1;
        }

        if (current > LatestVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {current} is newer than this build supports ({LatestVersion}). " +
                "Use a newer build of SmartFileOrganizer or point it at a different database.");
        }

        var pending = _migrations.Where(m => m.Version > current).ToList();
        if (pending.Count == 0) return current;

        // Steps rebuild tables and copy rows that older builds left without a parent (the job deletion missed some
        // tables until step 19 removed them). With foreign keys enforced such a copy would fail, so they are off for
        // the steps and restored afterwards. The pragma has no effect inside a transaction, so it runs out here.
        var foreignKeys = await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA foreign_keys", cancellationToken: ct));
        if (foreignKeys != 0)
            await connection.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys=OFF", cancellationToken: ct));
        try
        {
            current = await ApplyAsync(connection, pending, current, ct);
        }
        finally
        {
            if (foreignKeys != 0)
                await connection.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys=ON", cancellationToken: CancellationToken.None));
        }

        return current;
    }

    private async Task<int> ApplyAsync(SqliteConnection connection, IReadOnlyList<ISchemaMigration> pending, int current, CancellationToken ct)
    {
        foreach (var step in pending)
        {
            _logger.LogInformation("Applying schema step {Version}: {Description}", step.Version, step.Description);

            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            try
            {
                await step.ApplyAsync(connection, tx, ct);
                await RecordVersionAsync(connection, tx, step.Version, ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }

            current = step.Version;
        }

        return current;
    }

    public static async Task<int> GetCurrentVersionAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        var tableExists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'db_version'",
            cancellationToken: ct));
        if (tableExists == 0) return 0;

        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(version), 0) FROM db_version", cancellationToken: ct));
    }

    private static async Task<bool> HasLegacySchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'scan_jobs'",
            cancellationToken: ct));
        return count > 0;
    }

    private static Task RecordVersionAsync(
        SqliteConnection connection, SqliteTransaction? transaction, int version, CancellationToken ct)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO db_version (version, applied_at) VALUES (@version, @appliedAt)",
            new { version, appliedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
            transaction,
            cancellationToken: ct));
    }

    private static IReadOnlyList<ISchemaMigration> Discover(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(t => typeof(ISchemaMigration).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Select(t => (ISchemaMigration)Activator.CreateInstance(t, nonPublic: true)!)
            .ToList();
    }

    private static IReadOnlyList<ISchemaMigration> Validate(IEnumerable<ISchemaMigration> migrations)
    {
        var ordered = migrations.OrderBy(m => m.Version).ToList();

        var duplicates = ordered.GroupBy(m => m.Version).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0)
        {
            var detail = string.Join("; ", duplicates.Select(g =>
                $"{g.Key}: {string.Join(", ", g.Select(m => m.GetType().Name))}"));
            throw new InvalidOperationException(
                $"Duplicate schema migration versions ({detail}). Renumber the newer step to the next free number.");
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Version != i + 1)
            {
                throw new InvalidOperationException(
                    $"Schema migration versions must be 1..N without gaps; expected {i + 1} but found " +
                    $"{ordered[i].Version} ({ordered[i].GetType().Name}).");
            }
        }

        return ordered;
    }
}
