namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 8: closes gaps around the sensitive-output boundary
/// (https://github.com/crack666/smart_file_organizer/issues/33).
/// <list type="bullet">
/// <item><c>directory_nodes.sensitive_markers</c> / <c>sensitive_rule</c>: markers of a folder the scan
/// skipped without entering (<c>.ssh</c>, <c>AppData</c>). Same token format as on <c>file_nodes</c>.</item>
/// </list>
/// Existing skipped folders are marked by the read commands, which evaluate the folder rules again.
/// </summary>
internal sealed class M0008_SensitiveGaps : SqlSchemaMigration
{
    public override int Version => 8;
    public override string Description => "Sensitive markers on skipped folders";

    protected override string Sql => """
        ALTER TABLE directory_nodes ADD COLUMN sensitive_markers TEXT;
        ALTER TABLE directory_nodes ADD COLUMN sensitive_rule TEXT;
        """;
}
