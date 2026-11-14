namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 27: the target status <c>no_target</c> (https://github.com/crack666/smart_file_organizer/issues/85). Rather no
/// target than a wrong one: when no existing folder is confirmed by content and no library holds the finding's kind of
/// content, the match stores that on purpose. SQLite cannot change a CHECK constraint, so <c>finding_targets</c> is
/// rebuilt with <c>no_target</c> among the allowed codes; ids and all columns are kept. The codes are written out here,
/// not taken from the enum, so this step keeps doing the same thing when the codes change later.
/// </summary>
internal sealed class M0027_FindingTargetNoTarget : SqlSchemaMigration
{
    public override int Version => 27;
    public override string Description => "Finding target status no_target";

    protected override string Sql => """
        CREATE TABLE finding_targets_m27 (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id             INTEGER NOT NULL REFERENCES scan_jobs(id),
            finding_key        TEXT    NOT NULL,
            input_hash         TEXT    NOT NULL,
            status             TEXT    NOT NULL CHECK (status IN ('existing_folder', 'new_folder', 'no_fitting_folder', 'content_unclear', 'no_model', 'no_target')),
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
        INSERT INTO finding_targets_m27
            (id, job_id, finding_key, input_hash, status, library_job_id, path, relative_path, certainty, content_check,
             content_status, reason, same_content_files, with_model, model, matched_at)
        SELECT id, job_id, finding_key, input_hash, status, library_job_id, path, relative_path, certainty, content_check,
               content_status, reason, same_content_files, with_model, model, matched_at
        FROM finding_targets;
        DROP TABLE finding_targets;
        ALTER TABLE finding_targets_m27 RENAME TO finding_targets;
        """;
}
