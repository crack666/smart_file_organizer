namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 34: the kind <c>discard</c> (https://github.com/crack666/smart_file_organizer/issues/105). Clutter goes into the
/// hold folder of its source without a copy anywhere; it needs neither target nor library. <c>plan_actions.kind</c> and
/// <c>recommended</c> list the kinds in a CHECK, and SQLite cannot change a CHECK, so the table is copied once as in
/// step 25; nothing else changes. <c>plan_steps</c> and <c>moves</c> need no change: their <c>target_path</c> may be
/// null already, which is what a held original of <c>discard</c> has.
/// </summary>
internal sealed class M0034_DiscardKind : SqlSchemaMigration
{
    public override int Version => 34;
    public override string Description => "Plan action kind discard";

    protected override string Sql => """
        CREATE TABLE plan_actions_new (
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
            kind              TEXT    NOT NULL
                CHECK (kind IN ('move', 'copy', 'quarantine', 'delete_source', 'discard', 'leave')),
            recommended       TEXT    NOT NULL
                CHECK (recommended IN ('move', 'copy', 'quarantine', 'delete_source', 'discard', 'leave')),
            target_path       TEXT,
            library_job_id    INTEGER REFERENCES scan_jobs(id) ON DELETE SET NULL,
            reason            TEXT    NOT NULL,
            note              TEXT,
            origin            TEXT    NOT NULL CHECK (origin IN ('rule', 'model', 'agent', 'human')),
            state             TEXT    NOT NULL
                CHECK (state IN ('proposed', 'approved', 'rejected', 'verified', 'executed', 'failed', 'undone')),
            sensitive         INTEGER NOT NULL DEFAULT 0,
            single_decision   INTEGER NOT NULL DEFAULT 0,
            decided_by        TEXT CHECK (decided_by IN ('human', 'agent')),
            decided_at        TEXT,
            updated_at        TEXT    NOT NULL
        );
        INSERT INTO plan_actions_new (id, plan_id, finding_key, finding_kind, unit, source_kind, source_path, relative_path,
            file_node_id, directory_node_id, files, bytes, kind, recommended, target_path, library_job_id, reason, note,
            origin, state, sensitive, single_decision, decided_by, decided_at, updated_at)
        SELECT id, plan_id, finding_key, finding_kind, unit, source_kind, source_path, relative_path,
            file_node_id, directory_node_id, files, bytes, kind, recommended, target_path, library_job_id, reason, note,
            origin, state, sensitive, single_decision, decided_by, decided_at, updated_at
        FROM plan_actions;
        DROP TABLE plan_actions;
        ALTER TABLE plan_actions_new RENAME TO plan_actions;
        CREATE INDEX idx_plan_actions_plan ON plan_actions(plan_id);
        """;
}
