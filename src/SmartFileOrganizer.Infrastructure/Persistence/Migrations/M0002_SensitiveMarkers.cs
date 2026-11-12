namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 2: sensitive-file markers (https://github.com/crack666/smart_file_organizer/issues/9).
/// <list type="bullet">
/// <item><c>file_nodes.sensitive_markers</c> / <c>sensitive_rule</c>: set by the rules that need no
/// model (name, extension, header line), so they also cover files the model never sees.</item>
/// <item><c>ai_results.sensitive_markers</c>: reported by the local model in the regular
/// classification call.</item>
/// </list>
/// Values are comma-separated fixed tokens (<c>zugangsdaten,schluessel</c>), <c>NULL</c> for none.
/// Existing rows are not backfilled here: the output guard re-evaluates the name rules on every
/// export, and a rescan fills the columns.
/// </summary>
internal sealed class M0002_SensitiveMarkers : SqlSchemaMigration
{
    public override int Version => 2;
    public override string Description => "Sensitive-file markers on file_nodes and ai_results";

    protected override string Sql => """
        ALTER TABLE file_nodes ADD COLUMN sensitive_markers TEXT;
        ALTER TABLE file_nodes ADD COLUMN sensitive_rule TEXT;
        ALTER TABLE ai_results ADD COLUMN sensitive_markers TEXT;
        """;
}
