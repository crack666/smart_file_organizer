namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 35: corrections of directory verdicts by a person (https://github.com/crack666/smart_file_organizer/issues/14), in
/// a table of their own as <c>user_overrides</c> for files. <c>directory_verdicts</c> keeps what rule or model said; the
/// readers put the correction over it.
/// </summary>
internal sealed class M0035_DirectoryOverrides : SqlSchemaMigration
{
    public override int Version => 35;
    public override string Description => "Directory verdicts set by a person";

    protected override string Sql => """
        CREATE TABLE directory_overrides (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            directory_node_id INTEGER NOT NULL UNIQUE REFERENCES directory_nodes(id),
            verdict           TEXT NOT NULL CHECK (verdict IN ('unit', 'uniform', 'mixed', 'empty', 'system', 'dump')),
            retention         TEXT CHECK (retention IN ('keep', 'review', 'disposable')),
            note              TEXT,
            overridden_at     TEXT NOT NULL
        );
        """;
}
