namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 25: executing and taking back a plan (https://github.com/crack666/smart_file_organizer/issues/12).
/// <list type="bullet">
/// <item><c>plan_actions</c> gets the state <c>verified</c> (copy written and checked, the original waits for the
/// separately confirmed removal). SQLite cannot change a CHECK, so the table is copied once; nothing else changes.</item>
/// <item><c>scan_jobs.keep_source</c>: the person's switch "leave the source untouched" for a whole job; then a move only
/// copies and nothing is removed.</item>
/// <item><c>plan_steps</c>: the journal. Every step is written as <c>started</c> before the file system is touched and
/// finished afterwards, so a run cut off in the middle is recognised. Rows go with the plan, and with the job.</item>
/// </list>
/// Codes are those of docs/glossary.md, section Plans.
/// </summary>
internal sealed class M0025_PlanExecution : SqlSchemaMigration
{
    public override int Version => 25;
    public override string Description => "Plan execution: journal, verified state, keep-source switch";

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
            kind              TEXT    NOT NULL CHECK (kind IN ('move', 'copy', 'quarantine', 'delete_source', 'leave')),
            recommended       TEXT    NOT NULL CHECK (recommended IN ('move', 'copy', 'quarantine', 'delete_source', 'leave')),
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

        ALTER TABLE scan_jobs ADD COLUMN keep_source INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE plan_steps (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            plan_id      INTEGER NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
            action_id    INTEGER NOT NULL REFERENCES plan_actions(id) ON DELETE CASCADE,
            kind         TEXT    NOT NULL CHECK (kind IN ('make_folder', 'copy', 'present', 'verify', 'hold', 'purge',
                                                          'restore', 'uncopy', 'remove_folder')),
            state        TEXT    NOT NULL CHECK (state IN ('started', 'done', 'failed')),
            source_path  TEXT    NOT NULL,
            target_path  TEXT,
            hold_path    TEXT,
            bytes        INTEGER NOT NULL DEFAULT 0,
            hash         TEXT,
            detail       TEXT,
            started_at   TEXT    NOT NULL,
            finished_at  TEXT
        );
        CREATE INDEX idx_plan_steps_plan ON plan_steps(plan_id);
        """;
}
