using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Infrastructure.Persistence.Migrations;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Foreign keys on every connection, complete deletion of a job, and the step that removes rows earlier
/// deletions left behind. Real SQLite files in a temp directory.
/// </summary>
public class DatabaseIntegrityTests : IDisposable
{
    /// <summary>Tables that hold no job data: the version record and checksums keyed by path for all jobs.</summary>
    private static readonly HashSet<string> NotPerJob = ["sqlite_sequence", "db_version", "content_fingerprints"];

    /// <summary>Tables whose rows outlive the job and only lose the reference to it.</summary>
    private static readonly HashSet<string> Detached = ["eval_cases", "eval_runs"];

    private const string T = "2026-11-13T10:00:00.0000000Z";

    private readonly string _dir;
    private readonly string _dbPath;

    public DatabaseIntegrityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_integrity_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "data.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<DatabaseContext> CreateDatabaseAsync()
    {
        var db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        await db.InitializeAsync();
        return db;
    }

    [Fact]
    public async Task EveryConnection_EnforcesForeignKeys()
    {
        var db = await CreateDatabaseAsync();

        // A new connection, as each repository call opens one: the pragma is on without anyone setting it.
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("PRAGMA foreign_keys"));

        var ex = await Assert.ThrowsAsync<SqliteException>(() => conn.ExecuteAsync(
            """
            INSERT INTO file_nodes (job_id, full_path, name, parent_path, root_path, relative_path, relative_dir,
                                    last_write_time, scanned_at)
            VALUES (999, '/x/a.txt', 'a.txt', '/x', '/x', 'a.txt', '', @T, @T)
            """, new { T }));
        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT
        Assert.Contains("FOREIGN KEY", ex.Message);
        Assert.Equal(0, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM file_nodes"));
    }

    [Fact]
    public async Task DeletingAJob_LeavesNoRowForItInAnyTable()
    {
        var db = await CreateDatabaseAsync();
        await using var conn = db.CreateConnection();
        await conn.OpenAsync();

        var a = await SeedJobAsync(conn, "/a");
        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")).Where(t => !NotPerJob.Contains(t)).ToList();
        var perJob = new Dictionary<string, long>();
        foreach (var table in tables)
            perJob[table] = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}");

        // Every table with job data has a row, so a table added later fails here until the seed (and the deletion) know it.
        Assert.All(tables, t => Assert.True(perJob[t] > 0, $"Table {t} has no row for the job: add it to SeedJobAsync and to ScanJobRepository.DeleteAsync"));

        var b = await SeedJobAsync(conn, "/b");
        // A comparison of the other job against this one points at files of both.
        var cross = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO comparisons (source_job_id, against_job_id, compared_at, complete) VALUES (@b, @a, @T, 1); SELECT last_insert_rowid();",
            new { a = a.Job, b = b.Job, T });
        await conn.ExecuteAsync(
            "INSERT INTO comparison_files (comparison_id, file_node_id, state, match_file_node_id) VALUES (@cross, @fb, 'backed_up', @fa)",
            new { cross, fb = b.File, fa = a.File });

        await new ScanJobRepository(db).DeleteAsync(a.Job);

        foreach (var table in tables)
        {
            var columns = (await conn.QueryAsync<string>($"SELECT name FROM pragma_table_info('{table}')")).ToList();
            foreach (var column in columns.Where(c => c == "job_id" || c.EndsWith("_job_id", StringComparison.Ordinal)))
                Assert.Equal(0, await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE {column} = @job", new { job = a.Job }));
            foreach (var column in columns.Where(c => c == "file_node_id" || c.EndsWith("_file_node_id", StringComparison.Ordinal)))
                Assert.Equal(0, await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE {column} = @file", new { file = a.File }));
            foreach (var column in columns.Where(c => c == "directory_node_id" || c.EndsWith("_directory_node_id", StringComparison.Ordinal)))
                Assert.Equal(0, await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE {column} = @dir", new { dir = a.Directory }));

            // The other job keeps all of its rows; kept test cases and runs of the deleted job stay as well.
            var expected = Detached.Contains(table) ? 2 * perJob[table] : perJob[table];
            Assert.True(expected == await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}"), $"Row count of {table}");
        }

        Assert.Empty(await conn.QueryAsync("PRAGMA foreign_key_check"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM eval_cases WHERE job_id IS NULL AND file_node_id IS NULL AND subject_path = '/a/d/f.txt'"));
    }

    [Fact]
    public async Task OldDatabase_WithRowsLeftByDeletedJobs_IsCleanedByStep19()
    {
        // A database at step 15, before the verdict table is rebuilt once more by step 16: rows of a deleted job
        // must neither stop that copy nor survive it.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False;"))
        {
            await conn.OpenAsync();
            var upTo15 = new SchemaMigrator(SchemaMigrator.BuiltIn.Where(m => m.Version <= 15), NullLogger.Instance);
            Assert.Equal(15, await upTo15.MigrateAsync(conn));

            var kept = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO scan_jobs (root_path, created_at) VALUES ('/kept', @T); SELECT last_insert_rowid();", new { T });
            var keptDir = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO directory_nodes (job_id, full_path, name, parent_path, root_path, relative_path, scanned_at)
                VALUES (@kept, '/kept/d', 'd', '/kept', '/kept', 'd', @T); SELECT last_insert_rowid();
                """, new { kept, T });
            await conn.ExecuteAsync(
                "INSERT INTO directory_verdicts (directory_node_id, verdict, source, profile_json, judged_at) VALUES (@keptDir, 'unit', 'rule', '{}', @T)",
                new { keptDir, T });

            // What the old deletion left: verdicts, samples, facts and checks of a job that is gone. Whether such rows
            // could be written depended on the SQLite build's default for foreign keys; here they are switched off.
            await conn.ExecuteAsync("PRAGMA foreign_keys=OFF");
            await conn.ExecuteAsync(
                """
                INSERT INTO directory_verdicts (directory_node_id, verdict, source, profile_json, judged_at) VALUES (4711, 'unit', 'rule', '{}', @T);
                INSERT INTO directory_samples (directory_node_id, seed, state, eligible_count, members_json, max_deviation_share, drawn_at)
                    VALUES (4711, 's', 'open', 1, '[]', 0.5, @T);
                INSERT INTO file_facts (file_node_id, fact, value, source, recorded_at) VALUES (4712, 'title', 'x', 'rule', @T);
                INSERT INTO result_checks (file_node_id, reasons, outcome, first_answer, checked_at) VALUES (4712, 'r', 'agreed', '{}', @T);
                INSERT INTO eval_cases (kind, origin, job_id, file_node_id, subject_path, expected, created_at, updated_at)
                    VALUES ('file_category', 'review_override', 4713, 4712, '/gone/f.txt', 'documents', @T, @T);
                """, new { T });
            Assert.NotEmpty(await conn.QueryAsync("PRAGMA foreign_key_check"));
        }

        var db = await CreateDatabaseAsync();
        await using var check = db.CreateConnection();
        await check.OpenAsync();

        Assert.Equal(SchemaMigrator.BuiltIn.Count, await SchemaMigrator.GetCurrentVersionAsync(check));
        Assert.Equal(1, await check.ExecuteScalarAsync<long>("PRAGMA foreign_keys"));
        Assert.Empty(await check.QueryAsync("PRAGMA foreign_key_check"));
        Assert.Equal(1, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM directory_verdicts"));
        Assert.Equal(0, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM directory_samples"));
        Assert.Equal(0, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM file_facts"));
        Assert.Equal(0, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM result_checks"));
        // The test case stays, without the job and file it no longer has.
        Assert.Equal(1, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM eval_cases WHERE job_id IS NULL AND file_node_id IS NULL"));
    }

    private sealed record SeededJob(long Job, long Directory, long File);

    /// <summary>One row in every table that holds job data, all belonging to a new job under <paramref name="root"/>.</summary>
    private static async Task<SeededJob> SeedJobAsync(SqliteConnection conn, string root)
    {
        var job = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO scan_jobs (root_path, created_at) VALUES (@root, @T); SELECT last_insert_rowid();", new { root, T });
        var dir = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO directory_nodes (job_id, full_path, name, parent_path, root_path, relative_path, scanned_at)
            VALUES (@job, @root || '/d', 'd', @root, @root, 'd', @T); SELECT last_insert_rowid();
            """, new { job, root, T });
        var file = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO file_nodes (job_id, full_path, name, parent_path, root_path, relative_path, relative_dir,
                                    last_write_time, scanned_at)
            VALUES (@job, @root || '/d/f.txt', 'f.txt', @root || '/d', @root, 'd/f.txt', 'd', @T, @T);
            SELECT last_insert_rowid();
            """, new { job, root, T });
        var comparison = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO comparisons (source_job_id, against_job_id, compared_at, complete) VALUES (@job, NULL, @T, 1); SELECT last_insert_rowid();",
            new { job, T });
        var bundle = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO bundles (job_id, kind, suggested_name, file_count, total_bytes, directory_count, empty_directory_count,
                                 reason, facts_json, built_at)
            VALUES (@job, 'folder', 'd', 1, 1, 1, 0, 'r', '{}', @T); SELECT last_insert_rowid();
            """, new { job, T });

        await conn.ExecuteAsync(
            """
            INSERT INTO ai_results (file_node_id, analyzed_at, inherited_from_directory_node_id) VALUES (@file, @T, @dir);
            INSERT INTO user_overrides (file_node_id, overridden_at) VALUES (@file, @T);
            INSERT INTO file_facts (file_node_id, fact, value, source, recorded_at) VALUES (@file, 'title', 'x', 'rule', @T);
            INSERT INTO file_decisions (file_node_id, job_id, worth, certainty, rule, basis, decided_at)
                VALUES (@file, @job, 'keep', 'likely', 'facts', '[]', @T);
            INSERT INTO result_checks (file_node_id, reasons, outcome, first_answer, checked_at) VALUES (@file, 'r', 'agreed', '{}', @T);
            INSERT INTO ai_directory_results (directory_node_id, phase, analyzed_at) VALUES (@dir, 'summary', @T);
            INSERT INTO directory_verdicts (directory_node_id, verdict, source, unit_directory_node_id, profile_json, judged_at)
                VALUES (@dir, 'unit', 'rule', @dir, '{}', @T);
            INSERT INTO directory_samples (directory_node_id, seed, state, eligible_count, members_json, max_deviation_share, drawn_at)
                VALUES (@dir, 's', 'open', 1, '[]', 0.5, @T);
            INSERT INTO comparison_files (comparison_id, file_node_id, state, match_file_node_id) VALUES (@comparison, @file, NULL, @file);
            INSERT INTO bundles (job_id, parent_bundle_id, kind, suggested_name, file_count, total_bytes, directory_count,
                                 empty_directory_count, reason, facts_json, built_at)
                VALUES (@job, @bundle, 'event', 'e', 1, 1, 0, 0, 'r', '{}', @T);
            INSERT INTO bundle_files (file_node_id, bundle_id) VALUES (@file, @bundle);
            INSERT INTO bundle_builds (job_id, built_at, settings_json, stats_json) VALUES (@job, @T, '{}', '{}');
            INSERT INTO library_advice (job_id, task, input_hash, created_at) VALUES (@job, 'review', 'h', @T);
            INSERT INTO finding_decisions (job_id, finding_key, kind, state, origin, decided_at, snapshot_json)
                VALUES (@job, 'k', 'sensitive', 'accepted', 'human', @T, '{}');
            INSERT INTO model_server_waits (job_id, since, checked_at, cause) VALUES (@job, @T, @T, 'busy');
            INSERT INTO file_links (job_id, full_path, name, parent_path, link_kind, scanned_at)
                VALUES (@job, @root || '/l', 'l', @root, 'symlink', @T);
            INSERT INTO eval_cases (kind, origin, job_id, file_node_id, subject_path, expected, created_at, updated_at)
                VALUES ('file_category', 'review_override', @job, @file, @root || '/d/f.txt', 'documents', @T, @T);
            INSERT INTO eval_runs (mode, started_at, finished_at, subject, job_id, settings_json, report_json)
                VALUES ('cases', @T, @T, 's', @job, '{}', '{}');
            """, new { job, dir, file, comparison, bundle, root, T });

        return new SeededJob(job, dir, file);
    }
}
