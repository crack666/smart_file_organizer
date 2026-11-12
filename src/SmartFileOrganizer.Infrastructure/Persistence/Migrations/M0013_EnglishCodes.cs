using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 13: the fixed values written by the first steps become the English codes of
/// https://github.com/crack666/smart_file_organizer/issues/35 (see <c>docs/glossary.md</c>).
/// <list type="bullet">
/// <item><c>directory_verdicts</c> is rebuilt, because its CHECK constraints name the old codes:
/// <c>verdict</c>, <c>source</c>, <c>retention</c> and <c>escalated_from</c> are mapped, ids and all other
/// columns are kept.</item>
/// <item>Marker lists (<c>file_nodes</c>, <c>ai_results</c>, <c>directory_nodes</c>, <c>eval_cases</c>) are
/// rewritten value by value: each comma-separated token is mapped on its own, unknown tokens stay.</item>
/// <item>Texts the program wrote from codes: the keep reason of a marked file, the reason of the container
/// rule (<c>subdirectories differ (8 ..., 3 ...)</c>), the profile hint <c>suggests</c>, the verdict copied into
/// <c>ai_directory_results.dominant_type</c>, the answers kept in <c>result_checks</c>, and expected and actual
/// values in the stored evaluation reports.</item>
/// </list>
/// Raw model answers (<c>raw_response</c>) stay as they were received. The old codes are listed here and
/// nowhere else in code, because this step must keep doing the same thing when the codes change later.
/// </summary>
internal sealed class M0013_EnglishCodes : ISchemaMigration
{
    public int Version => 13;
    public string Description => "English codes for verdict, retention, verdict source and sensitive markers";

    private static readonly Dictionary<string, string> Verdicts = new(StringComparer.Ordinal)
    {
        ["einheit"] = "unit",
        ["einheitlich"] = "uniform",
        ["gemischt"] = "mixed",
        ["leer"] = "empty",
        ["system"] = "system"
    };

    private static readonly Dictionary<string, string> Retentions = new(StringComparer.Ordinal)
    {
        ["behalten"] = "keep",
        ["pruefen"] = "review",
        ["entbehrlich"] = "disposable"
    };

    private static readonly Dictionary<string, string> Sources = new(StringComparer.Ordinal)
    {
        ["regel"] = "rule",
        ["modell"] = "model"
    };

    private static readonly Dictionary<string, string> Markers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zugangsdaten"] = "credentials",
        ["schluessel"] = "keys",
        ["private_nachricht"] = "private_message",
        ["ausweis"] = "identity",
        ["finanzen"] = "finance",
        ["gesundheit"] = "health"
    };

    private static readonly Regex KeepReason = new(@"^Marked as sensitive \((?<list>[^)]*)\); never temporary\.$", RegexOptions.CultureInvariant);
    private static readonly Regex ContainerCounts = new(@"(?<=subdirectories differ \()(?<counts>[^)]*)(?=\))", RegexOptions.CultureInvariant);
    private static readonly Regex CountItem = new(@"(?<n>\d+) (?<code>[a-z]+)", RegexOptions.CultureInvariant);
    private static readonly Regex RaisedFrom = new(@"\braised from (?<code>[a-z]+)\b", RegexOptions.CultureInvariant);
    private static readonly Regex MissedOrExtra = new(@"\b(?<what>missed|extra) (?<list>[a-z_,]+)", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions ReportJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        connection.CreateFunction<string?, string?>("sfo_m13_markers", MapMarkerList, isDeterministic: true);
        connection.CreateFunction<string?, string?>("sfo_m13_keep_reason", MapKeepReason, isDeterministic: true);
        connection.CreateFunction<string?, string?>("sfo_m13_container_reason", MapContainerReason, isDeterministic: true);
        connection.CreateFunction<string?, string?>("sfo_m13_answer", MapAnswerJson, isDeterministic: true);
        connection.CreateFunction<string?, string?>("sfo_m13_report", MapReportJson, isDeterministic: true);

        await ExecuteAsync(connection, transaction, RebuildVerdictsSql(), ct);

        foreach (var table in new[] { "file_nodes", "ai_results", "directory_nodes", "eval_cases" })
            await ExecuteAsync(connection, transaction, Rewrite(table, "sensitive_markers", "sfo_m13_markers"), ct);

        await ExecuteAsync(connection, transaction, Rewrite("ai_results", "keep_reason", "sfo_m13_keep_reason"), ct);
        await ExecuteAsync(connection, transaction, Rewrite("result_checks", "first_answer", "sfo_m13_answer"), ct);
        await ExecuteAsync(connection, transaction, Rewrite("result_checks", "second_answer", "sfo_m13_answer"), ct);
        await ExecuteAsync(connection, transaction, Rewrite("eval_runs", "report_json", "sfo_m13_report"), ct);
        await ExecuteAsync(connection, transaction,
            Rewrite("directory_verdicts", "reason", "sfo_m13_container_reason") + " AND rule = 'container'", ct);

        foreach (var (old, code) in Verdicts.Where(v => v.Key != v.Value))
        {
            await ExecuteAsync(connection, transaction, $"""
                UPDATE directory_verdicts SET profile_json = REPLACE(profile_json, '"suggests":"{old}"', '"suggests":"{code}"')
                WHERE profile_json LIKE '%"suggests":"{old}"%';
                UPDATE ai_directory_results SET dominant_type = '{code}'
                WHERE phase = 'pre_assessment' AND dominant_type = '{old}';
                """, ct);
        }
    }

    private static string Rewrite(string table, string column, string function) =>
        $"UPDATE {table} SET {column} = {function}({column}) WHERE {column} IS NOT NULL AND {column} <> {function}({column})";

    private static string Case(string column, Dictionary<string, string> map) =>
        $"CASE {column} {string.Concat(map.Select(kv => $"WHEN '{kv.Key}' THEN '{kv.Value}' "))}ELSE {column} END";

    private static string RebuildVerdictsSql() => $"""
        CREATE TABLE directory_verdicts_m13 (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            directory_node_id       INTEGER NOT NULL UNIQUE REFERENCES directory_nodes(id),
            verdict                 TEXT CHECK (verdict IN ('unit', 'uniform', 'mixed', 'empty', 'system')),
            source                  TEXT NOT NULL CHECK (source IN ('rule', 'model')),
            rule                    TEXT,
            purpose                 TEXT,
            retention               TEXT CHECK (retention IN ('keep', 'review', 'disposable')),
            reason                  TEXT,
            unit_directory_node_id  INTEGER REFERENCES directory_nodes(id),
            profile_json            TEXT NOT NULL,
            model_used              TEXT,
            raw_response            TEXT,
            judged_at               TEXT NOT NULL,
            error                   TEXT,
            escalated_from          TEXT,
            escalation_reason       TEXT,
            CHECK (verdict IS NOT NULL OR error IS NOT NULL)
        );
        INSERT INTO directory_verdicts_m13
            (id, directory_node_id, verdict, source, rule, purpose, retention, reason, unit_directory_node_id,
             profile_json, model_used, raw_response, judged_at, error, escalated_from, escalation_reason)
        SELECT id, directory_node_id, {Case("verdict", Verdicts)}, {Case("source", Sources)}, rule, purpose,
               {Case("retention", Retentions)}, reason, unit_directory_node_id, profile_json, model_used,
               raw_response, judged_at, error, {Case("escalated_from", Verdicts)}, escalation_reason
        FROM directory_verdicts;
        DROP TABLE directory_verdicts;
        ALTER TABLE directory_verdicts_m13 RENAME TO directory_verdicts;
        CREATE INDEX idx_dir_verdicts_unit ON directory_verdicts(unit_directory_node_id);
        """;

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Mappings (internal for the migration test) ─────────────────────

    /// <summary>A comma-separated marker list, token by token; unknown tokens and <c>none</c> stay.</summary>
    internal static string? MapMarkerList(string? list) =>
        list is null ? null : string.Join(',', list.Split(',').Select(t => Markers.TryGetValue(t.Trim(), out var c) ? c : t));

    internal static string? MapKeepReason(string? text)
    {
        if (text is null) return null;
        var m = KeepReason.Match(text);
        return m.Success ? $"Marked as sensitive ({MapMarkerList(m.Groups["list"].Value)}); never temporary." : text;
    }

    internal static string? MapContainerReason(string? text) =>
        text is null ? null : ContainerCounts.Replace(text, m => CountItem.Replace(m.Value,
            i => $"{i.Groups["n"].Value} {(Verdicts.TryGetValue(i.Groups["code"].Value, out var c) ? c : i.Groups["code"].Value)}"));

    /// <summary>An answer in <c>result_checks</c>: the marker list and the keep reason.</summary>
    internal static string? MapAnswerJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject node) return json;
        var changed = false;
        if (node["sensitive_markers"] is JsonValue markers && markers.TryGetValue<string>(out var list))
        {
            var mapped = MapMarkerList(list);
            if (mapped != list) { node["sensitive_markers"] = mapped; changed = true; }
        }
        if (node["keep_reason"] is JsonValue reason && reason.TryGetValue<string>(out var text))
        {
            var mapped = MapKeepReason(text);
            if (mapped != text) { node["keep_reason"] = mapped; changed = true; }
        }
        return changed ? node.ToJsonString() : json;
    }

    /// <summary>A stored evaluation report: expected and actual values and the details that name codes.</summary>
    internal static string? MapReportJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject report
            || report["failures"] is not JsonArray failures) return json;
        var changed = false;
        foreach (var failure in failures.OfType<JsonObject>())
        {
            var kind = failure["kind"]?.GetValue<string>();
            Func<string, string>? map = kind switch
            {
                "directory_verdict" => v => Verdicts.GetValueOrDefault(v, v),
                "sensitive" => v => MapMarkerList(v)!,
                _ => null
            };
            if (map is null) continue;
            foreach (var field in new[] { "expected", "actual" })
            {
                if (failure[field] is JsonValue value && value.TryGetValue<string>(out var s) && map(s) != s)
                {
                    failure[field] = map(s);
                    changed = true;
                }
            }
            if (failure["detail"] is JsonValue detailValue && detailValue.TryGetValue<string>(out var detail))
            {
                var mapped = MapContainerReason(detail)!;
                mapped = RaisedFrom.Replace(mapped, m => $"raised from {Verdicts.GetValueOrDefault(m.Groups["code"].Value, m.Groups["code"].Value)}");
                mapped = MissedOrExtra.Replace(mapped, m => $"{m.Groups["what"].Value} {MapMarkerList(m.Groups["list"].Value)}");
                if (mapped != detail) { failure["detail"] = mapped; changed = true; }
            }
        }
        return changed ? report.ToJsonString(ReportJson) : json;
    }
}
