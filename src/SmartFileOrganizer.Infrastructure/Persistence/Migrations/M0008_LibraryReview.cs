namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 8: judging a library by its structure (https://github.com/crack666/smart_file_organizer/issues/29).
/// <c>scan_jobs.structure_only</c> marks a job scanned without opening any file; <c>library_advice</c>
/// keeps the model's answer to <c>sfo library review</c> and <c>sfo library propose</c>, so the same
/// question is not asked again while the facts it was asked about are unchanged.
/// </summary>
internal sealed class M0008_LibraryReview : SqlSchemaMigration
{
    public override int Version => 8;
    public override string Description => "Structure-only scans and stored library advice";

    protected override string Sql => """
        ALTER TABLE scan_jobs ADD COLUMN structure_only INTEGER NOT NULL DEFAULT 0;

        CREATE TABLE IF NOT EXISTS library_advice (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id       INTEGER NOT NULL,
            task         TEXT    NOT NULL,
            input_hash   TEXT    NOT NULL,
            model        TEXT,
            created_at   TEXT    NOT NULL,
            content      TEXT,
            raw_response TEXT,
            error        TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_library_advice_job_task ON library_advice (job_id, task, input_hash);
        """;
}
