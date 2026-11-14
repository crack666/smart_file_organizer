namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 26: target matches of findings (https://github.com/crack666/smart_file_organizer/issues/83).
/// <c>sfo findings targets</c> runs the match of <c>sfo library match</c> (rules and content check) for every finding
/// that should move and keeps the answer here: the target folder in a library, or why there is none. One row per job
/// and finding key; <c>input_hash</c> names what the match rested on (files, their descriptions, the libraries), so a
/// finding whose data changed counts as not matched and a second run asks nothing the first already answered.
/// Rows go with the job.
/// </summary>
internal sealed class M0026_FindingTargets : SqlSchemaMigration
{
    public override int Version => 26;
    public override string Description => "Target matches of findings";

    protected override string Sql => """
        CREATE TABLE finding_targets (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id             INTEGER NOT NULL REFERENCES scan_jobs(id),
            finding_key        TEXT    NOT NULL,
            input_hash         TEXT    NOT NULL,
            status             TEXT    NOT NULL CHECK (status IN ('existing_folder', 'new_folder', 'no_fitting_folder', 'content_unclear', 'no_model')),
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
        """;
}
