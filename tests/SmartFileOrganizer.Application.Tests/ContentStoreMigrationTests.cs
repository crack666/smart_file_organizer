using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Infrastructure.Persistence.Migrations;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Step 22 (https://github.com/crack666/smart_file_organizer/issues/38) copies <c>ai_results</c> to widen the CHECK of
/// <c>origin</c> by <c>reused</c>: every row and column stays, the other codes are still checked.
/// </summary>
public class ContentStoreMigrationTests : IDisposable
{
    private const string T = "2026-11-13T10:00:00.0000000Z";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sfo_m22_{Guid.NewGuid():N}");

    public ContentStoreMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Step22_KeepsEveryResult_AndAcceptsReused()
    {
        await using var conn = new SqliteConnection($"Data Source={Path.Combine(_dir, "data.db")};Pooling=False;");
        await conn.OpenAsync();
        Assert.Equal(21, await new SchemaMigrator(SchemaMigrator.BuiltIn.Where(m => m.Version <= 21), NullLogger.Instance).MigrateAsync(conn));

        await conn.ExecuteAsync(
            """
            INSERT INTO scan_jobs (root_path, created_at) VALUES ('/a', @T);
            INSERT INTO file_nodes (job_id, full_path, name, parent_path, root_path, relative_path, relative_dir, last_write_time, scanned_at)
                VALUES (1, '/a/x.txt', 'x.txt', '/a', '/a', 'x.txt', '', @T, @T),
                       (1, '/a/y.txt', 'y.txt', '/a', '/a', 'y.txt', '', @T, @T);
            INSERT INTO ai_results (file_node_id, category, importance, confidence, summary, model_used, analyzed_at, origin,
                                    keep_hint, certainty, attempts, evidence, sensitive_markers)
                VALUES (1, 4, 2, 0.9, 'a letter', 'm', @T, 'model', 'worth_keeping', 'likely', 2, '{}', 'finance');
            INSERT INTO user_overrides (file_node_id, overridden_category, overridden_at) VALUES (1, 5, @T);
            """, new { T });
        var columnsBefore = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('ai_results') ORDER BY cid")).ToList();

        Assert.Equal(22, await new SchemaMigrator(SchemaMigrator.BuiltIn.Where(m => m.Version <= 22), NullLogger.Instance).MigrateAsync(conn));

        Assert.Equal(columnsBefore, (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('ai_results') ORDER BY cid")).ToList());
        var row = await conn.QuerySingleAsync<(long Id, string Summary, string Origin, string KeepHint, string Certainty, long Attempts, string Markers)>(
            "SELECT id, summary, origin, keep_hint, certainty, attempts, sensitive_markers FROM ai_results WHERE file_node_id = 1");
        Assert.Equal((1L, "a letter", "model", "worth_keeping", "likely", 2L, "finance"), row);
        Assert.Null(await conn.ExecuteScalarAsync<string>("SELECT origin FROM user_overrides"));

        await conn.ExecuteAsync("INSERT INTO ai_results (file_node_id, analyzed_at, origin) VALUES (2, @T, 'reused')", new { T });
        var ex = await Assert.ThrowsAsync<SqliteException>(() =>
            conn.ExecuteAsync("UPDATE ai_results SET origin = 'guessed' WHERE file_node_id = 2"));
        Assert.Contains("CHECK", ex.Message);
        // The file id stays unique: one result per file.
        await Assert.ThrowsAsync<SqliteException>(() =>
            conn.ExecuteAsync("INSERT INTO ai_results (file_node_id, analyzed_at) VALUES (2, @T)", new { T }));
    }
}
