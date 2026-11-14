namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 32: who decided a target, and the inbox of a library (https://github.com/crack666/smart_file_organizer/issues/95).
/// The steering model decides where a finding goes (<c>sfo findings place</c>); the local model only describes. Both
/// <c>finding_targets</c> and <c>finding_target_parts</c> get <c>decided_by</c> (<c>rules</c>, <c>local_model</c>,
/// <c>controller</c>) and the status <c>inbox</c>; <c>finding_targets.content_status</c> may now be <c>controller</c>,
/// which needs no rebuild (the column has no CHECK). SQLite cannot change a CHECK constraint, so both tables are rebuilt;
/// ids and all columns are kept. Matches stored before were made by the rules alone (<c>with_model</c> 0) or with the
/// local model. The codes are written out here, not taken from the enums, so this step keeps doing the same thing when
/// the codes change later.
/// </summary>
internal sealed class M0032_TargetDecisions : SqlSchemaMigration
{
    public override int Version => 32;
    public override string Description => "Finding target decided_by, status inbox";

    protected override string Sql => """
        CREATE TABLE finding_targets_m32 (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id             INTEGER NOT NULL REFERENCES scan_jobs(id),
            finding_key        TEXT    NOT NULL,
            input_hash         TEXT    NOT NULL,
            status             TEXT    NOT NULL CHECK (status IN ('existing_folder', 'new_folder', 'no_fitting_folder', 'content_unclear',
                                                                  'no_model', 'no_target', 'split', 'excluded', 'inbox')),
            library_job_id     INTEGER,
            path               TEXT,
            relative_path      TEXT,
            certainty          TEXT,
            content_check      TEXT    NOT NULL,
            content_status     TEXT    NOT NULL,
            reason             TEXT    NOT NULL,
            same_content_files INTEGER NOT NULL DEFAULT 0,
            with_model         INTEGER NOT NULL,
            model              TEXT,
            matched_at         TEXT    NOT NULL,
            decided_by         TEXT    NOT NULL DEFAULT 'rules' CHECK (decided_by IN ('rules', 'local_model', 'controller')),
            UNIQUE (job_id, finding_key)
        );
        INSERT INTO finding_targets_m32
            (id, job_id, finding_key, input_hash, status, library_job_id, path, relative_path, certainty, content_check,
             content_status, reason, same_content_files, with_model, model, matched_at, decided_by)
        SELECT id, job_id, finding_key, input_hash, status, library_job_id, path, relative_path, certainty, content_check,
               content_status, reason, same_content_files, with_model, model, matched_at,
               CASE WHEN with_model <> 0 THEN 'local_model' ELSE 'rules' END
        FROM finding_targets;
        DROP TABLE finding_targets;
        ALTER TABLE finding_targets_m32 RENAME TO finding_targets;

        CREATE TABLE finding_target_parts_m32 (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id         INTEGER NOT NULL REFERENCES scan_jobs(id),
            finding_key    TEXT    NOT NULL,
            part_index     INTEGER NOT NULL,
            file_ids       TEXT    NOT NULL,
            label          TEXT    NOT NULL,
            status         TEXT    NOT NULL CHECK (status IN ('existing_folder', 'new_folder', 'no_fitting_folder', 'content_unclear',
                                                              'no_model', 'no_target', 'excluded', 'inbox')),
            library_job_id INTEGER,
            path           TEXT,
            relative_path  TEXT,
            certainty      TEXT,
            content_check  TEXT    NOT NULL,
            reason         TEXT    NOT NULL,
            decided_by     TEXT    NOT NULL DEFAULT 'rules' CHECK (decided_by IN ('rules', 'local_model', 'controller')),
            UNIQUE (job_id, finding_key, part_index)
        );
        INSERT INTO finding_target_parts_m32
            (id, job_id, finding_key, part_index, file_ids, label, status, library_job_id, path, relative_path, certainty,
             content_check, reason, decided_by)
        SELECT p.id, p.job_id, p.finding_key, p.part_index, p.file_ids, p.label, p.status, p.library_job_id, p.path,
               p.relative_path, p.certainty, p.content_check, p.reason,
               COALESCE((SELECT t.decided_by FROM finding_targets t WHERE t.job_id = p.job_id AND t.finding_key = p.finding_key), 'rules')
        FROM finding_target_parts p;
        DROP TABLE finding_target_parts;
        ALTER TABLE finding_target_parts_m32 RENAME TO finding_target_parts;
        """;
}
