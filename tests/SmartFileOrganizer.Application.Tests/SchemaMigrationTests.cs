using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Infrastructure.Persistence.Migrations;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Schema migrations against real SQLite files in a temp directory.
/// </summary>
public class SchemaMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public SchemaMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_migr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "data.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False;");
        conn.Open();
        return conn;
    }

    private static Task<long> CountTable(SqliteConnection conn, string name) =>
        conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name", new { name });

    /// <summary>Recreates a database as the app created it before numbered migrations:
    /// all step-1 tables, an empty db_version table, and some user data.</summary>
    private async Task CreateLegacyDatabaseAsync()
    {
        await using var conn = Open();
        await using (var tx = conn.BeginTransaction())
        {
            await SchemaMigrator.BuiltIn[0].ApplyAsync(conn, tx, CancellationToken.None);
            tx.Commit();
        }

        await conn.ExecuteAsync(
            "INSERT INTO scan_jobs (root_path, status, created_at) VALUES ('/legacy', 3, '2026-01-01T00:00:00Z')");
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM db_version"));
    }

    [Fact]
    public void BuiltIn_StartsWithInitialSchema_AndIsContiguous()
    {
        var migrator = new SchemaMigrator(SchemaMigrator.BuiltIn, NullLogger.Instance);

        Assert.Equal(1, migrator.Migrations[0].Version);
        Assert.Equal(Enumerable.Range(1, migrator.Migrations.Count), migrator.Migrations.Select(m => m.Version));
        Assert.Equal(migrator.Migrations.Count, migrator.LatestVersion);
    }

    [Fact]
    public async Task EmptyDatabase_IsMigratedToLatest_WithOneVersionRowPerStep()
    {
        var db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);

        var version = await db.InitializeAsync();

        var latest = SchemaMigrator.BuiltIn.Count;
        Assert.Equal(latest, version);

        await using var conn = Open();
        var recorded = (await conn.QueryAsync<int>("SELECT version FROM db_version ORDER BY version")).ToList();
        Assert.Equal(Enumerable.Range(1, latest), recorded);

        foreach (var table in new[] { "scan_jobs", "directory_nodes", "file_nodes", "ai_results",
                                      "user_overrides", "ai_directory_results" })
        {
            Assert.Equal(1, await CountTable(conn, table));
        }
    }

    [Fact]
    public async Task SecondInitialize_IsNoOp()
    {
        var db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        var first = await db.InitializeAsync();
        var second = await db.InitializeAsync();

        Assert.Equal(first, second);
        await using var conn = Open();
        Assert.Equal(first, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM db_version"));
    }

    [Fact]
    public async Task LegacyDatabaseWithoutVersionRow_IsTreatedAsVersion1_AndKeepsData()
    {
        await CreateLegacyDatabaseAsync();

        var version = await new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance).InitializeAsync();

        Assert.Equal(SchemaMigrator.BuiltIn.Count, version);
        await using var conn = Open();
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM db_version WHERE version = 1"));
        Assert.Equal("/legacy", await conn.ExecuteScalarAsync<string>("SELECT root_path FROM scan_jobs"));
    }

    [Fact]
    public async Task LegacyDatabase_GetsLaterStepsApplied()
    {
        await CreateLegacyDatabaseAsync();
        var steps = SchemaMigrator.BuiltIn.Append(new AddNoteColumnStep(SchemaMigrator.BuiltIn.Count + 1));
        var migrator = new SchemaMigrator(steps, NullLogger.Instance);

        await using var conn = Open();
        var version = await migrator.MigrateAsync(conn);

        Assert.Equal(migrator.LatestVersion, version);
        var columns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('scan_jobs')")).ToList();
        Assert.Contains("test_note", columns);
        Assert.Equal("/legacy", await conn.ExecuteScalarAsync<string>("SELECT root_path FROM scan_jobs"));
    }

    [Fact]
    public async Task FailingStep_IsRolledBack_AndVersionStaysAtPreviousStep()
    {
        var before = SchemaMigrator.BuiltIn.Count;
        var steps = SchemaMigrator.BuiltIn.Append(new FailingStep(before + 1));
        var migrator = new SchemaMigrator(steps, NullLogger.Instance);

        await using var conn = Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.MigrateAsync(conn));

        Assert.Equal(before, await SchemaMigrator.GetCurrentVersionAsync(conn));
        Assert.Equal(0, await CountTable(conn, "half_done"));
    }

    [Fact]
    public async Task DatabaseNewerThanBuild_IsRefused()
    {
        await new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance).InitializeAsync();
        await using (var conn = Open())
        {
            await conn.ExecuteAsync(
                "INSERT INTO db_version (version, applied_at) VALUES (999, '2026-01-01T00:00:00Z')");
        }

        var db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.InitializeAsync());
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void DuplicateVersions_AreRejected()
    {
        var steps = SchemaMigrator.BuiltIn.Append(new AddNoteColumnStep(1));
        var ex = Assert.Throws<InvalidOperationException>(() => new SchemaMigrator(steps, NullLogger.Instance));
        Assert.Contains("Duplicate", ex.Message);
    }

    [Fact]
    public void GapInVersions_IsRejected()
    {
        var steps = SchemaMigrator.BuiltIn.Append(new AddNoteColumnStep(SchemaMigrator.BuiltIn.Count + 2));
        Assert.Throws<InvalidOperationException>(() => new SchemaMigrator(steps, NullLogger.Instance));
    }

    private sealed class AddNoteColumnStep(int version) : SqlSchemaMigration
    {
        public override int Version => version;
        public override string Description => "test: add scan_jobs.test_note";
        protected override string Sql => "ALTER TABLE scan_jobs ADD COLUMN test_note TEXT;";
    }

    private sealed class FailingStep(int version) : ISchemaMigration
    {
        public int Version => version;
        public string Description => "test: create a table, then fail";

        public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
        {
            await connection.ExecuteAsync("CREATE TABLE half_done (id INTEGER)", transaction: transaction);
            throw new InvalidOperationException("simulated failure");
        }
    }
}
