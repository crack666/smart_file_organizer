using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;

namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 22: the content store (https://github.com/crack666/smart_file_organizer/issues/38). An assessment of a file
/// belongs to its content, not to its path, so the same content in a second job, under another name or on another old
/// disk is not sent to the model again.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>content_assessments</c>: what the model said about one content (full SHA-256 and size), with the model and
/// the prompt version it was said with, and whether the keep hint was asked. Only what belongs to the content is kept:
/// category, importance, confidence, certainty, one-sentence summary, keep hint, model markers and the description facts
/// of stage 1. No target, no folder verdict, no finding, no decision. No job reference: the rows outlive every job.</item>
/// <item><c>content_corrections</c>: the category a person set for one content; it follows the content into every job.</item>
/// <item><c>user_overrides.origin</c>: <c>human</c> (null for older rows) or <c>reused</c> for a correction carried over
/// from the content store.</item>
/// <item><c>ai_results.origin</c> gains the code <c>reused</c>. SQLite cannot change a CHECK constraint in place, so the
/// table is copied once with the widened constraint; every column and row stays as it was.</item>
/// </list>
/// </remarks>
internal sealed class M0022_ContentStore : ISchemaMigration
{
    public int Version => 22;
    public string Description => "Content store: assessments and corrections by content";

    private const string CreateSql = """
        CREATE TABLE content_assessments (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            content_hash      TEXT NOT NULL,
            size              INTEGER NOT NULL,
            model             TEXT NOT NULL,
            prompt_version    TEXT NOT NULL,
            keep_hint_asked   INTEGER NOT NULL,
            category          INTEGER NOT NULL,
            importance        INTEGER NOT NULL,
            confidence        REAL NOT NULL,
            certainty         TEXT CHECK (certainty IN ('certain', 'likely', 'uncertain', 'review')),
            summary           TEXT,
            keep_hint         TEXT CHECK (keep_hint IN ('temporary', 'worth_keeping')),
            keep_reason       TEXT,
            sensitive_markers TEXT,
            facts_json        TEXT NOT NULL,
            created_at        TEXT NOT NULL,
            last_used_at      TEXT,
            use_count         INTEGER NOT NULL DEFAULT 0,
            UNIQUE (content_hash, size, model, prompt_version, keep_hint_asked)
        );

        CREATE TABLE content_corrections (
            content_hash      TEXT NOT NULL,
            size              INTEGER NOT NULL,
            category          INTEGER NOT NULL,
            note              TEXT,
            corrected_at      TEXT NOT NULL,
            PRIMARY KEY (content_hash, size)
        );

        ALTER TABLE user_overrides ADD COLUMN origin TEXT CHECK (origin IN ('human', 'reused'));
        """;

    private static readonly Regex OriginCheck = new(@"CHECK\s*\(\s*origin\s+IN\s*\((?<codes>[^)]*)\)\s*\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CreateHead = new(@"^\s*CREATE\s+TABLE\s+(IF\s+NOT\s+EXISTS\s+)?[""`\[]?ai_results[""`\]]?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(CreateSql, transaction: transaction, cancellationToken: ct));

        var tableSql = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'ai_results'", transaction: transaction, cancellationToken: ct))
            ?? throw new InvalidOperationException("Table ai_results not found.");
        var check = OriginCheck.Match(tableSql);
        if (!check.Success)
            throw new InvalidOperationException("The CHECK constraint of ai_results.origin was not found; step 22 cannot widen it.");
        if (check.Groups["codes"].Value.Contains("'reused'", StringComparison.Ordinal))
            return;

        var widened = tableSql[..check.Index]
                      + $"CHECK (origin IN ({check.Groups["codes"].Value.TrimEnd()}, 'reused'))"
                      + tableSql[(check.Index + check.Length)..];
        if (!CreateHead.IsMatch(widened))
            throw new InvalidOperationException("The definition of ai_results has an unexpected form; step 22 cannot copy it.");
        var createCopy = CreateHead.Replace(widened, "CREATE TABLE ai_results_m22", 1);

        var columns = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM pragma_table_info('ai_results') ORDER BY cid", transaction: transaction, cancellationToken: ct))).ToList();
        var indexes = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'ai_results' AND sql IS NOT NULL",
            transaction: transaction, cancellationToken: ct))).ToList();
        var list = string.Join(", ", columns.Select(c => $"\"{c}\""));

        await connection.ExecuteAsync(new CommandDefinition($"""
            {createCopy};
            INSERT INTO ai_results_m22 ({list}) SELECT {list} FROM ai_results;
            DROP TABLE ai_results;
            ALTER TABLE ai_results_m22 RENAME TO ai_results;
            """, transaction: transaction, cancellationToken: ct));
        foreach (var index in indexes)
            await connection.ExecuteAsync(new CommandDefinition(index, transaction: transaction, cancellationToken: ct));
    }
}
