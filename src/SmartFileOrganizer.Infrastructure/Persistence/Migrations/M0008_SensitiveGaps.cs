namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 8: closes gaps around the sensitive-output boundary
/// (https://github.com/crack666/smart_file_organizer/issues/33).
/// <list type="bullet">
/// <item><c>directory_nodes.sensitive_markers</c> / <c>sensitive_rule</c>: markers of a folder the scan
/// skipped without entering (<c>.ssh</c>, <c>AppData</c>). Same token format as on <c>file_nodes</c>.</item>
/// <item><c>file_nodes.sensitive_rules_version</c>: version of the file rules that ran at scan time
/// (<c>SensitiveFileRules.Version</c>). Existing rows stay <c>NULL</c>: the next analysis runs the rules that
/// read the file (header lines) for them before any model call.</item>
/// <item><c>ai_directory_results.marked_inputs</c>: JSON list of the marked files that entered a model-written
/// directory summary as their neutral line only. A summary whose list misses a file marked since then is
/// withheld and written again by the next analysis. Existing rows stay <c>NULL</c> (nothing known).</item>
/// </list>
/// Existing skipped folders are marked by the read commands, which evaluate the folder rules again.
/// </summary>
internal sealed class M0008_SensitiveGaps : SqlSchemaMigration
{
    public override int Version => 8;
    public override string Description => "Sensitive markers on skipped folders; rule version per file; marked inputs of directory summaries";

    protected override string Sql => """
        ALTER TABLE directory_nodes ADD COLUMN sensitive_markers TEXT;
        ALTER TABLE directory_nodes ADD COLUMN sensitive_rule TEXT;
        ALTER TABLE file_nodes ADD COLUMN sensitive_rules_version INTEGER;
        ALTER TABLE ai_directory_results ADD COLUMN marked_inputs TEXT;
        """;
}
