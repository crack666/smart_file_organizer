namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 27: the memory of the tool (https://github.com/crack666/smart_file_organizer/issues/86).
/// <list type="bullet">
/// <item><c>places</c>: every scanned root with its drive (label, serial number, file system, as far as known), its last
/// completed job and the numbers of that scan. A place outlives its jobs: deleting a job moves <c>last_job_id</c> to the
/// newest other job of the place, or to null.</item>
/// <item><c>moves</c>: the move history. A row is written when an original goes into the hold folder and marked when
/// it comes back or is purged. It holds paths and the checksum only and outlives jobs and plans, so
/// <c>sfo where</c> still answers after the job is deleted.</item>
/// <item><c>scan_changes</c>: what <c>sfo scan --refresh</c> found new, changed, missing or moved against the previous job
/// of the place. Rows go with the job.</item>
/// <item><c>notice_entries</c>: the entries of move notices a scan found in the scanned folders. Rows go with the job.</item>
/// <item><c>plan_steps.kind</c> gains <c>notice</c>. SQLite cannot change a CHECK, so the table is copied once.</item>
/// </list>
/// Codes are those of docs/glossary.md, section Places.
/// </summary>
internal sealed class M0027_Places : SqlSchemaMigration
{
    public override int Version => 27;
    public override string Description => "Places, move history, refresh changes, move notices";

    protected override string Sql => """
        CREATE TABLE plan_steps_new (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            plan_id      INTEGER NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
            action_id    INTEGER NOT NULL REFERENCES plan_actions(id) ON DELETE CASCADE,
            kind         TEXT    NOT NULL CHECK (kind IN ('make_folder', 'copy', 'present', 'verify', 'hold', 'purge',
                                                          'restore', 'uncopy', 'remove_folder', 'notice')),
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
        INSERT INTO plan_steps_new (id, plan_id, action_id, kind, state, source_path, target_path, hold_path, bytes, hash,
            detail, started_at, finished_at)
        SELECT id, plan_id, action_id, kind, state, source_path, target_path, hold_path, bytes, hash,
            detail, started_at, finished_at
        FROM plan_steps;
        DROP TABLE plan_steps;
        ALTER TABLE plan_steps_new RENAME TO plan_steps;
        CREATE INDEX idx_plan_steps_plan ON plan_steps(plan_id);

        CREATE TABLE places (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            root_path      TEXT    NOT NULL UNIQUE,
            role           TEXT    NOT NULL CHECK (role IN ('source', 'library')),
            volume_label   TEXT,
            volume_id      TEXT,
            volume_format  TEXT,
            last_job_id    INTEGER REFERENCES scan_jobs(id) ON DELETE SET NULL,
            last_scan_at   TEXT,
            files          INTEGER NOT NULL DEFAULT 0,
            bytes          INTEGER NOT NULL DEFAULT 0,
            first_seen_at  TEXT    NOT NULL
        );

        CREATE TABLE moves (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            plan_id      INTEGER NOT NULL,
            action_id    INTEGER NOT NULL,
            source_path  TEXT    NOT NULL,
            target_path  TEXT,
            hash         TEXT,
            bytes        INTEGER NOT NULL DEFAULT 0,
            moved_at     TEXT    NOT NULL,
            returned_at  TEXT,
            purged_at    TEXT
        );
        CREATE INDEX idx_moves_source ON moves(source_path);
        CREATE INDEX idx_moves_target ON moves(target_path);
        CREATE INDEX idx_moves_hash ON moves(hash);

        CREATE TABLE scan_changes (
            job_id           INTEGER NOT NULL REFERENCES scan_jobs(id),
            previous_job_id  INTEGER REFERENCES scan_jobs(id),
            full_path        TEXT    NOT NULL,
            change           TEXT    NOT NULL CHECK (change IN ('new', 'changed', 'missing', 'moved')),
            size             INTEGER NOT NULL DEFAULT 0,
            target_path      TEXT,
            plan_id          INTEGER,
            moved_at         TEXT,
            PRIMARY KEY (job_id, full_path)
        );

        CREATE TABLE notice_entries (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id       INTEGER NOT NULL REFERENCES scan_jobs(id),
            notice_path  TEXT    NOT NULL,
            source_path  TEXT    NOT NULL,
            target_path  TEXT    NOT NULL,
            plan_id      INTEGER NOT NULL,
            moved_at     TEXT    NOT NULL
        );
        CREATE INDEX idx_notice_entries_source ON notice_entries(source_path);
        """;
}
