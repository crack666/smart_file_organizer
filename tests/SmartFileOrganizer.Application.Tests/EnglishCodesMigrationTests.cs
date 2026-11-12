using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Infrastructure.Persistence.Migrations;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// A database written before the switch to English codes is migrated, and afterwards holds only codes
/// of https://github.com/crack666/smart_file_organizer/issues/35 and reads back through the repositories.
/// </summary>
public class EnglishCodesMigrationTests : IDisposable
{
    /// <summary>Every value the earlier steps wrote in German. Raw model answers are exempt: they stay as received.</summary>
    private static readonly Regex OldCode = new(
        @"\b(einheit|einheitlich|gemischt|leer|behalten|pruefen|entbehrlich|regel|modell|zugangsdaten|schluessel|private_nachricht|ausweis|finanzen|gesundheit)\b");

    private readonly string _dir;
    private readonly string _dbPath;

    public EnglishCodesMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_m13_{Guid.NewGuid():N}");
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

    private static ISchemaMigration EnglishStep =>
        SchemaMigrator.BuiltIn.Single(m => m.Description.StartsWith("English codes", StringComparison.Ordinal));

    [Fact]
    public async Task OldDatabase_IsMigrated_AndHoldsOnlyTheNewCodes()
    {
        await CreateOldDatabaseAsync();

        var db = new DatabaseContext(_dbPath, NullLogger<DatabaseContext>.Instance);
        Assert.Equal(SchemaMigrator.BuiltIn.Count, await db.InitializeAsync());

        await using (var conn = Open())
        {
            var verdicts = (await conn.QueryAsync<(long Id, string? Verdict, string Source, string? Retention, string? EscalatedFrom)>(
                "SELECT id, verdict, source, retention, escalated_from FROM directory_verdicts ORDER BY id")).ToList();
            Assert.Equal(
                [(10L, "unit", "rule", "review", null), (11L, "uniform", "model", "keep", null),
                 (12L, "mixed", "model", "review", "uniform"), (13L, "empty", "rule", "disposable", null),
                 (14L, "system", "rule", "keep", null), (15L, null, "model", null, null)],
                verdicts);

            Assert.Equal("No direct files; subdirectories differ (8 uniform, 3 mixed), so each is handled on its own.",
                await conn.ExecuteScalarAsync<string>("SELECT reason FROM directory_verdicts WHERE id = 13"));
            // The model wrote this reason; it is not a rule text and stays as it was.
            Assert.Equal("Sieht einheitlich aus.", await conn.ExecuteScalarAsync<string>("SELECT reason FROM directory_verdicts WHERE id = 11"));
            Assert.Contains("\"suggests\":\"uniform\"", await conn.ExecuteScalarAsync<string>("SELECT profile_json FROM directory_verdicts WHERE id = 11"));
            Assert.Equal("{\"verdict\":\"gemischt\"}", await conn.ExecuteScalarAsync<string>("SELECT raw_response FROM directory_verdicts WHERE id = 12"));

            Assert.Equal(["credentials,keys", "private_message,unknown_token"],
                (await conn.QueryAsync<string>("SELECT sensitive_markers FROM file_nodes WHERE sensitive_markers IS NOT NULL ORDER BY id")).ToList());
            Assert.Equal("identity,finance,health", await conn.ExecuteScalarAsync<string>("SELECT sensitive_markers FROM ai_results WHERE id = 1"));
            Assert.Equal("Marked as sensitive (identity,finance,health); never temporary.",
                await conn.ExecuteScalarAsync<string>("SELECT keep_reason FROM ai_results WHERE id = 1"));
            Assert.Equal("keys", await conn.ExecuteScalarAsync<string>("SELECT sensitive_markers FROM directory_nodes WHERE id = 2"));
            Assert.Equal("finance", await conn.ExecuteScalarAsync<string>("SELECT sensitive_markers FROM eval_cases"));
            Assert.Equal("unit", await conn.ExecuteScalarAsync<string>("SELECT dominant_type FROM ai_directory_results WHERE phase = 'pre_assessment'"));
            Assert.Equal("Photo", await conn.ExecuteScalarAsync<string>("SELECT dominant_type FROM ai_directory_results WHERE phase = 'summary'"));

            // The table was rebuilt with the new CHECK constraints: an old code can no longer be written.
            await Assert.ThrowsAsync<SqliteException>(() => conn.ExecuteAsync(
                "UPDATE directory_verdicts SET verdict = 'gemischt' WHERE id = 10"));
            Assert.Equal(1L, await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_dir_verdicts_unit'"));

            foreach (var (place, value) in await AllTextCellsAsync(conn))
                Assert.False(OldCode.IsMatch(value) && place != "directory_verdicts.reason#11", $"{place} still holds an old code: {value}");
        }

        // Everything reads back through the repositories (an unknown code would throw).
        var verdictRows = await new DirectoryVerdictRepository(db).GetForJobAsync(1);
        Assert.Equal(6, verdictRows.Count);
        Assert.Contains(verdictRows, v => v.Verdict == DirectoryVerdictKind.Mixed && v.EscalatedFrom == DirectoryVerdictKind.Uniform);
        Assert.Contains(verdictRows, v => v.Profile.Suggests == DirectoryVerdictKind.Uniform);

        var classifications = new ClassificationRepository(db);
        var result = await classifications.GetByFileNodeIdAsync(1);
        Assert.Equal(SensitiveMarker.Identity | SensitiveMarker.Finance | SensitiveMarker.Health, result!.SensitiveMarkers);
        var check = Assert.Single(await classifications.GetChecksAsync(1));
        Assert.Equal(SensitiveMarker.Credentials, check.First.SensitiveMarkers);
        Assert.Equal("Marked as sensitive (credentials); never temporary.", check.First.KeepReason);

        var run = await new EvalRunRepository(db).GetAsync(1);
        var failures = EvalRunService.ReadReport(run!).Failures;
        Assert.Equal(("unit", "uniform"), (failures[0].Expected, failures[0].Actual));
        Assert.StartsWith("raised from uniform (", failures[1].Detail);
        Assert.Equal(("keys", "none", "missed keys; file not in the scan"), (failures[2].Expected, failures[2].Actual, failures[2].Detail));
        Assert.Equal(("photo", "screenshot"), (failures[3].Expected, failures[3].Actual));
    }

    /// <summary>Builds the schema as it was before the English step, with rows as the old code wrote them.</summary>
    private async Task CreateOldDatabaseAsync()
    {
        await using var conn = Open();
        var before = SchemaMigrator.BuiltIn.Where(m => m.Version < EnglishStep.Version);
        await new SchemaMigrator(before, NullLogger.Instance).MigrateAsync(conn);

        const string t = "2026-11-01T10:00:00.0000000Z";
        await conn.ExecuteAsync($$"""
            INSERT INTO scan_jobs (id, root_path, created_at) VALUES (1, 'C:/t', '{{t}}');
            INSERT INTO directory_nodes (id, job_id, full_path, name, parent_path, root_path, relative_path, scanned_at, sensitive_markers)
            VALUES (1, 1, 'C:/t', 't', 'C:/', 'C:/t', '.', '{{t}}', NULL),
                   (2, 1, 'C:/t/a', 'a', 'C:/t', 'C:/t', 'a', '{{t}}', 'schluessel'),
                   (3, 1, 'C:/t/b', 'b', 'C:/t', 'C:/t', 'b', '{{t}}', NULL),
                   (4, 1, 'C:/t/c', 'c', 'C:/t', 'C:/t', 'c', '{{t}}', NULL),
                   (5, 1, 'C:/t/d', 'd', 'C:/t', 'C:/t', 'd', '{{t}}', NULL),
                   (6, 1, 'C:/t/e', 'e', 'C:/t', 'C:/t', 'e', '{{t}}', NULL);
            INSERT INTO file_nodes (id, job_id, full_path, name, parent_path, root_path, relative_path, relative_dir, last_write_time, scanned_at, sensitive_markers)
            VALUES (1, 1, 'C:/t/a/x.pdf', 'x.pdf', 'C:/t/a', 'C:/t', 'a/x.pdf', 'a', '{{t}}', '{{t}}', 'zugangsdaten,schluessel'),
                   (2, 1, 'C:/t/a/y.txt', 'y.txt', 'C:/t/a', 'C:/t', 'a/y.txt', 'a', '{{t}}', '{{t}}', 'private_nachricht,unknown_token'),
                   (3, 1, 'C:/t/a/z.txt', 'z.txt', 'C:/t/a', 'C:/t', 'a/z.txt', 'a', '{{t}}', '{{t}}', NULL);
            INSERT INTO ai_results (id, file_node_id, analyzed_at, sensitive_markers, keep_reason, raw_response)
            VALUES (1, 1, '{{t}}', 'ausweis,finanzen,gesundheit',
                    'Marked as sensitive (ausweis,finanzen,gesundheit); never temporary.', '{"sensitive":["ausweis"]}');
            INSERT INTO result_checks (file_node_id, reasons, outcome, first_answer, checked_at)
            VALUES (1, 'low_confidence', 'agreed',
                    '{"category":"Document","importance":"High","confidence":0.4,"keep_reason":"Marked as sensitive (zugangsdaten); never temporary.","analyzed_at":"{{t}}","sensitive_markers":"zugangsdaten"}', '{{t}}');
            INSERT INTO ai_directory_results (directory_node_id, phase, dominant_type, analyzed_at)
            VALUES (2, 'pre_assessment', 'einheit', '{{t}}'), (3, 'summary', 'Photo', '{{t}}');
            INSERT INTO directory_verdicts (id, directory_node_id, verdict, source, rule, retention, reason, profile_json, judged_at, error, escalated_from, raw_response)
            VALUES (10, 1, 'einheit', 'regel', 'marker', 'pruefen', 'Markers of a project.', '{"file_count":0}', '{{t}}', NULL, NULL, NULL),
                   (11, 2, 'einheitlich', 'modell', NULL, 'behalten', 'Sieht einheitlich aus.', '{"file_count":8,"suggests":"einheitlich"}', '{{t}}', NULL, NULL, NULL),
                   (12, 3, 'gemischt', 'modell', NULL, 'pruefen', 'Mixed.', '{"file_count":3,"suggests":"gemischt"}', '{{t}}', NULL, 'einheitlich', '{"verdict":"gemischt"}'),
                   (13, 4, 'leer', 'regel', 'container', 'entbehrlich',
                    'No direct files; subdirectories differ (8 einheitlich, 3 gemischt), so each is handled on its own.', '{}', '{{t}}', NULL, NULL, NULL),
                   (14, 5, 'system', 'regel', 'hidden', 'behalten', 'Hidden.', '{}', '{{t}}', NULL, NULL, NULL),
                   (15, 6, NULL, 'modell', NULL, NULL, NULL, '{}', '{{t}}', 'timeout', NULL, NULL);
            INSERT INTO eval_cases (kind, origin, subject_path, expected, sensitive_markers, created_at, updated_at)
            VALUES ('file_category', 'review_override', 'C:/t/a/x.pdf', 'invoice', 'finanzen', '{{t}}', '{{t}}');
            INSERT INTO eval_runs (mode, started_at, finished_at, subject, settings_json, report_json)
            VALUES ('corpus', '{{t}}', '{{t}}', 'small v1 seed 36', '{}', '{{ReportJson()}}');
            """);
    }

    private static string ReportJson() =>
        """
        {"scores":[],"failures":[
        {"kind":"directory_verdict","key":"directory_verdict:a","path":"a","outcome":"wrong","expected":"einheit","actual":"einheitlich","decided_by":"rule container","detail":"No direct files; subdirectories differ (2 einheit, 1 leer), so each is handled on its own."},
        {"kind":"directory_verdict","key":"directory_verdict:b","path":"b","outcome":"wrong","expected":"einheitlich","actual":"gemischt","decided_by":"sample","detail":"raised from einheitlich (model m): 1 deviate."},
        {"kind":"sensitive","key":"sensitive:c","path":"c","outcome":"missing","expected":"schluessel","actual":"none","decided_by":null,"detail":"missed schluessel; file not in the scan"},
        {"kind":"file_category","key":"file_category:d","path":"d","outcome":"wrong","expected":"photo","actual":"screenshot","decided_by":"model m","detail":null}
        ],"untracked_count":0,"untracked":[]}
        """.ReplaceLineEndings(string.Empty);

    private static async Task<List<(string Place, string Value)>> AllTextCellsAsync(SqliteConnection conn)
    {
        var cells = new List<(string, string)>();
        var tables = (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table'")).ToList();
        foreach (var table in tables)
        {
            var columns = (await conn.QueryAsync<string>($"SELECT name FROM pragma_table_info('{table}')"))
                .Where(c => c != "raw_response").ToList();
            foreach (var column in columns)
            {
                var rows = await conn.QueryAsync<(long Id, object? Value)>($"SELECT rowid, \"{column}\" FROM \"{table}\"");
                foreach (var (id, value) in rows)
                    if (value is string s) cells.Add(($"{table}.{column}#{id}", s));
            }
        }
        return cells;
    }
}
