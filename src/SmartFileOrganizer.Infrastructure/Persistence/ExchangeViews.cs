using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using SmartFileOrganizer.Application.Exchange;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// The named views <c>v_files</c> and <c>v_directories</c> for reading the database directly
/// (https://github.com/crack666/smart_file_organizer/issues/34). The tables stay internal and change with every schema
/// step; the columns of these views keep their names and meanings (docs/data-format.md, "Reading the database").
/// </summary>
/// <remarks>
/// <para>The views are not schema steps. <see cref="SchemaMigrator"/> drops them before it applies a step (a step that
/// copies a table and renames it back would otherwise fail on a view that names the table), and
/// <see cref="EnsureAsync"/> creates them after it, and again whenever their text differs from the one stored, so they
/// always follow the tables and the enums of this build.</para>
/// <para>Stored integers (category, importance, file type, status) are shown as their codes, written from the enums
/// here, so a new enum value reaches the view without a schema step. The views show what the database holds: they do
/// not pass the output guard. Whatever leaves the machine goes through <c>sfo export</c>.</para>
/// </remarks>
public static class ExchangeViews
{
    public const string Files = "v_files";
    public const string Directories = "v_directories";

    public static IReadOnlyList<string> Names { get; } = [Files, Directories];

    /// <summary>The column names of <see cref="Files"/>, in order. Only appended to; a test pins them.</summary>
    public static IReadOnlyList<string> FileColumns { get; } =
    [
        "job_id", "file_id", "directory_id", "relative_path", "full_path", "name", "extension", "file_type", "size_bytes",
        "last_write_time", "status", "analysis_skip", "rule_markers", "sensitive_rule", "model_markers", "category",
        "category_source", "result_origin", "importance", "confidence", "certainty", "keep_hint", "summary", "model",
        "analyzed_at", "error"
    ];

    /// <summary>The column names of <see cref="Directories"/>, in order. Only appended to; a test pins them.</summary>
    public static IReadOnlyList<string> DirectoryColumns { get; } =
    [
        "job_id", "directory_id", "relative_path", "full_path", "name", "parent_path", "depth", "status", "direct_files",
        "direct_bytes", "subdirs", "sensitive_markers", "sensitive_rule", "analysis_skip", "verdict", "verdict_source",
        "verdict_rule", "retention", "purpose", "reason", "escalated_from", "model", "judged_at", "verdict_error"
    ];

    /// <summary>The <c>CREATE VIEW</c> statements, built from the enums of this build.</summary>
    public static IReadOnlyDictionary<string, string> Sql { get; } = new Dictionary<string, string>
    {
        [Files] = $"""
            CREATE VIEW {Files} AS
            SELECT f.job_id AS job_id,
                   f.id AS file_id,
                   d.id AS directory_id,
                   {Portable("f.relative_path")} AS relative_path,
                   f.full_path AS full_path,
                   f.name AS name,
                   f.extension AS extension,
                   {Codes<FileType>("f.file_type")} AS file_type,
                   f.size AS size_bytes,
                   f.last_write_time AS last_write_time,
                   {Codes<FileNodeStatus>("f.status")} AS status,
                   f.analysis_skip AS analysis_skip,
                   f.sensitive_markers AS rule_markers,
                   f.sensitive_rule AS sensitive_rule,
                   a.sensitive_markers AS model_markers,
                   CASE WHEN o.id IS NOT NULL THEN {Codes<FileCategory>("o.overridden_category")}
                        ELSE {Codes<FileCategory>("a.category")} END AS category,
                   CASE WHEN o.id IS NOT NULL THEN COALESCE(o.origin, 'human')
                        WHEN a.id IS NOT NULL THEN COALESCE(a.origin, 'model') END AS category_source,
                   CASE WHEN a.id IS NOT NULL THEN COALESCE(a.origin, 'model') END AS result_origin,
                   {Codes<Importance>("a.importance")} AS importance,
                   a.confidence AS confidence,
                   a.certainty AS certainty,
                   a.keep_hint AS keep_hint,
                   a.summary AS summary,
                   a.model_used AS model,
                   a.analyzed_at AS analyzed_at,
                   a.error AS error
            FROM file_nodes f
            LEFT JOIN ai_results a ON a.file_node_id = f.id
            LEFT JOIN user_overrides o ON o.file_node_id = f.id
            LEFT JOIN directory_nodes d ON d.job_id = f.job_id AND d.full_path = f.parent_path
            """,
        [Directories] = $"""
            CREATE VIEW {Directories} AS
            SELECT d.job_id AS job_id,
                   d.id AS directory_id,
                   {Portable("d.relative_path")} AS relative_path,
                   d.full_path AS full_path,
                   d.name AS name,
                   d.parent_path AS parent_path,
                   d.depth AS depth,
                   {Codes<DirectoryStatus>("d.dir_status", s => s.ToCode())} AS status,
                   d.direct_file_count AS direct_files,
                   d.total_size AS direct_bytes,
                   d.subdir_count AS subdirs,
                   d.sensitive_markers AS sensitive_markers,
                   d.sensitive_rule AS sensitive_rule,
                   d.analysis_skip AS analysis_skip,
                   v.verdict AS verdict,
                   v.source AS verdict_source,
                   v.rule AS verdict_rule,
                   v.retention AS retention,
                   v.purpose AS purpose,
                   v.reason AS reason,
                   v.escalated_from AS escalated_from,
                   v.model_used AS model,
                   v.judged_at AS judged_at,
                   v.error AS verdict_error
            FROM directory_nodes d
            LEFT JOIN directory_verdicts v ON v.directory_node_id = d.id
            """
    };

    /// <summary>Drops both views; the migrator calls it before a schema step.</summary>
    public static async Task DropAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        foreach (var name in Names)
            await connection.ExecuteAsync(new CommandDefinition($"DROP VIEW IF EXISTS {name}", cancellationToken: ct));
    }

    /// <summary>Creates the views, or replaces one whose stored text differs. Does nothing when both are current.</summary>
    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        foreach (var (name, sql) in Sql)
        {
            var stored = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT sql FROM sqlite_master WHERE type = 'view' AND name = @name", new { name }, cancellationToken: ct));
            if (stored == sql) continue;

            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition($"DROP VIEW IF EXISTS {name}", transaction: tx, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(sql, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
    }

    /// <summary>The stored relative path with <c>/</c>; <c>.</c> for the root (as in the export).</summary>
    private static string Portable(string column) =>
        $"CASE WHEN {column} IN ('', '.') THEN '.' ELSE replace({column}, char(92), '/') END";

    private static string Codes<TEnum>(string column) where TEnum : struct, Enum =>
        Codes<TEnum>(column, ExchangeFormat.Snake);

    private static string Codes<TEnum>(string column, Func<TEnum, string> code) where TEnum : struct, Enum
    {
        var sb = new StringBuilder("CASE ").Append(column);
        foreach (var value in Enum.GetValues<TEnum>())
            sb.Append(" WHEN ").Append(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture))
              .Append(" THEN '").Append(code(value)).Append('\'');
        return sb.Append(" END").ToString();
    }
}
