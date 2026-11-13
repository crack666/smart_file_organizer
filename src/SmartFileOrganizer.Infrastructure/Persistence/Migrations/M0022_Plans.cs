namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 22: plans (https://github.com/crack666/smart_file_organizer/issues/11). <c>sfo plan create</c> turns the
/// accepted findings of a source job into actions; a person or a steering agent changes and confirms them. Nothing is
/// executed here. <c>libraries</c> and <c>notes</c> of a plan are JSON lists. An action points at its file or folder
/// while the scan row exists (a rescan of the folder sets the reference to null, the path stays) and at the library
/// whose root holds its target (deleting that library job sets it to null). Codes are those of docs/glossary.md,
/// section Plans; the states were reserved there since the decision table.
/// </summary>
internal sealed class M0022_Plans : SqlSchemaMigration
{
    public override int Version => 22;
    public override string Description => "Plans: actions from accepted findings, to be confirmed";

    protected override string Sql => """
        CREATE TABLE plans (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id      INTEGER NOT NULL REFERENCES scan_jobs(id),
            created_at  TEXT    NOT NULL,
            libraries   TEXT    NOT NULL,
            notes       TEXT    NOT NULL
        );
        CREATE INDEX idx_plans_job ON plans(job_id);

        CREATE TABLE plan_actions (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            plan_id           INTEGER NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
            finding_key       TEXT    NOT NULL,
            finding_kind      TEXT    NOT NULL
                CHECK (finding_kind IN ('not_backed_up', 'in_noise', 'misplaced', 'sensitive', 'duplicate')),
            unit              TEXT    NOT NULL,
            source_kind       TEXT    NOT NULL CHECK (source_kind IN ('file', 'folder')),
            source_path       TEXT    NOT NULL,
            relative_path     TEXT    NOT NULL,
            file_node_id      INTEGER REFERENCES file_nodes(id) ON DELETE SET NULL,
            directory_node_id INTEGER REFERENCES directory_nodes(id) ON DELETE SET NULL,
            files             INTEGER NOT NULL,
            bytes             INTEGER NOT NULL,
            kind              TEXT    NOT NULL CHECK (kind IN ('move', 'copy', 'quarantine', 'delete_source', 'leave')),
            recommended       TEXT    NOT NULL CHECK (recommended IN ('move', 'copy', 'quarantine', 'delete_source', 'leave')),
            target_path       TEXT,
            library_job_id    INTEGER REFERENCES scan_jobs(id) ON DELETE SET NULL,
            reason            TEXT    NOT NULL,
            note              TEXT,
            origin            TEXT    NOT NULL CHECK (origin IN ('rule', 'model', 'agent', 'human')),
            state             TEXT    NOT NULL
                CHECK (state IN ('proposed', 'approved', 'rejected', 'executed', 'failed', 'undone')),
            sensitive         INTEGER NOT NULL DEFAULT 0,
            single_decision   INTEGER NOT NULL DEFAULT 0,
            decided_by        TEXT CHECK (decided_by IN ('human', 'agent')),
            decided_at        TEXT,
            updated_at        TEXT    NOT NULL
        );
        CREATE INDEX idx_plan_actions_plan ON plan_actions(plan_id);
        """;
}
