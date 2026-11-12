namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 2: one verdict per directory (einheit, einheitlich, gemischt, leer, system) together with
/// the profile it was based on and whether a rule or the model decided.
/// See https://github.com/crack666/smart_file_organizer/issues/7.
/// </summary>
internal sealed class M0002_DirectoryVerdicts : SqlSchemaMigration
{
    public override int Version => 2;
    public override string Description => "Directory verdicts with profile and source";

    protected override string Sql => """
        CREATE TABLE directory_verdicts (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            directory_node_id       INTEGER NOT NULL UNIQUE REFERENCES directory_nodes(id),
            verdict                 TEXT CHECK (verdict IN ('einheit', 'einheitlich', 'gemischt', 'leer', 'system')),
            source                  TEXT NOT NULL CHECK (source IN ('regel', 'modell')),
            rule                    TEXT,
            purpose                 TEXT,
            retention               TEXT CHECK (retention IN ('behalten', 'pruefen', 'entbehrlich')),
            reason                  TEXT,
            unit_directory_node_id  INTEGER REFERENCES directory_nodes(id),
            profile_json            TEXT NOT NULL,
            model_used              TEXT,
            raw_response            TEXT,
            judged_at               TEXT NOT NULL,
            error                   TEXT,
            CHECK (verdict IS NOT NULL OR error IS NOT NULL)
        );
        CREATE INDEX idx_dir_verdicts_unit ON directory_verdicts(unit_directory_node_id);
        """;
}
