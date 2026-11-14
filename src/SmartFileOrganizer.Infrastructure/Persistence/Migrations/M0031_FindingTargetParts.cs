namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 31: targets per part of a mixed finding, and findings left out of the target match on purpose
/// (https://github.com/crack666/smart_file_organizer/issues/85). <c>finding_targets</c> is rebuilt with the codes
/// <c>split</c> and <c>excluded</c> among the allowed statuses (SQLite cannot change a CHECK constraint); ids and all
/// columns are kept. <c>finding_target_parts</c> holds one row per part of a <c>split</c> finding: its files (ids as a
/// JSON list) and its own place or why it has none. The codes are written out here, not taken from the enum, so this step
/// keeps doing the same thing when the codes change later.
/// </summary>
internal sealed class M0031_FindingTargetParts : SqlSchemaMigration
{
    public override int Version => 31;
    public override string Description => "Finding target parts, statuses split and excluded";

    protected override string Sql => """
        CREATE TABLE finding_targets_m31 (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id             INTEGER NOT NULL REFERENCES scan_jobs(id),
            finding_key        TEXT    NOT NULL,
            input_hash         TEXT    NOT NULL,
            status             TEXT    NOT NULL CHECK (status IN ('existing_folder', 'new_folder', 'no_fitting_folder', 'content_unclear',
                                                                  'no_model', 'no_target', 'split', 'excluded')),
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
            UNIQUE (job_id, finding_key)
        );
        INSERT INTO finding_targets_m31
            (id, job_id, finding_key, input_hash, status, library_job_id, path, relative_path, certainty, content_check,
             content_status, reason, same_content_files, with_model, model, matched_at)
        SELECT id, job_id, finding_key, input_hash, status, library_job_id, path, relative_path, certainty, content_check,
               content_status, reason, same_content_files, with_model, model, matched_at
        FROM finding_targets;
        DROP TABLE finding_targets;
        ALTER TABLE finding_targets_m31 RENAME TO finding_targets;

        CREATE TABLE finding_target_parts (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id         INTEGER NOT NULL REFERENCES scan_jobs(id),
            finding_key    TEXT    NOT NULL,
            part_index     INTEGER NOT NULL,
            file_ids       TEXT    NOT NULL,
            label          TEXT    NOT NULL,
            status         TEXT    NOT NULL CHECK (status IN ('existing_folder', 'new_folder', 'no_fitting_folder', 'content_unclear',
                                                              'no_model', 'no_target', 'excluded')),
            library_job_id INTEGER,
            path           TEXT,
            relative_path  TEXT,
            certainty      TEXT,
            content_check  TEXT    NOT NULL,
            reason         TEXT    NOT NULL,
            UNIQUE (job_id, finding_key, part_index)
        );
        """;
}
