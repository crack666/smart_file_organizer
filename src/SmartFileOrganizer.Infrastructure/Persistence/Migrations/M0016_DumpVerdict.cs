namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 16: the directory verdict <c>dump</c> (https://github.com/crack666/smart_file_organizer/issues/59).
/// A copy of a phone, a computer or a user profile is no longer a <c>unit</c>: it keeps a verdict of its own,
/// its worth-keeping parts are judged, its noise is skipped. SQLite cannot change a CHECK constraint, so
/// <c>directory_verdicts</c> is rebuilt with <c>dump</c> among the allowed codes; ids and all columns are kept.
/// </summary>
/// <remarks>
/// Verdicts stored before this step stay as they are: a dump judged as a unit then remains one in that job
/// until the job is scanned again. The verdict codes are written out here, not taken from the enum, because
/// this step must keep doing the same thing when the codes change later.
/// </remarks>
internal sealed class M0016_DumpVerdict : SqlSchemaMigration
{
    public override int Version => 16;
    public override string Description => "Directory verdict dump";

    protected override string Sql => """
        CREATE TABLE directory_verdicts_m16 (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            directory_node_id       INTEGER NOT NULL UNIQUE REFERENCES directory_nodes(id),
            verdict                 TEXT CHECK (verdict IN ('unit', 'uniform', 'mixed', 'empty', 'system', 'dump')),
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
        INSERT INTO directory_verdicts_m16
            (id, directory_node_id, verdict, source, rule, purpose, retention, reason, unit_directory_node_id,
             profile_json, model_used, raw_response, judged_at, error, escalated_from, escalation_reason)
        SELECT id, directory_node_id, verdict, source, rule, purpose, retention, reason, unit_directory_node_id,
               profile_json, model_used, raw_response, judged_at, error, escalated_from, escalation_reason
        FROM directory_verdicts;
        DROP TABLE directory_verdicts;
        ALTER TABLE directory_verdicts_m16 RENAME TO directory_verdicts;
        CREATE INDEX idx_dir_verdicts_unit ON directory_verdicts(unit_directory_node_id);
        """;
}
