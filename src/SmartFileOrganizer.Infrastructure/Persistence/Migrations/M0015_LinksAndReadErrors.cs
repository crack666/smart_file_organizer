namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 15: links and read errors of the scan
/// (https://github.com/crack666/smart_file_organizer/issues/57).
/// <list type="bullet">
/// <item><c>directory_nodes.link_kind</c> / <c>link_target</c>: a folder that is a junction, symbolic link or
/// mount point (code of <c>LinkKind</c>) and where it points. Set on a link that was not followed
/// (<c>dir_status</c> link) and on one a <c>follow_link</c> rule let the scan enter.</item>
/// <item><c>directory_nodes.scan_error_cause</c>, <c>file_nodes.scan_error_cause</c> / <c>scan_error</c>: why a
/// folder or a file could not be read (code of <c>ScanErrorCause</c>). Existing error folders get a cause
/// derived from their stored message.</item>
/// <item><c>file_links</c>: files that are themselves links. They are kept out of <c>file_nodes</c>, so no
/// count, comparison or analysis takes them for content.</item>
/// </list>
/// Jobs scanned before this step may have followed links; only a new scan corrects their counts.
/// </summary>
internal sealed class M0015_LinksAndReadErrors : SqlSchemaMigration
{
    public override int Version => 15;
    public override string Description => "Links found by the scan; cause of scan read errors";

    protected override string Sql => """
        ALTER TABLE directory_nodes ADD COLUMN link_kind TEXT;
        ALTER TABLE directory_nodes ADD COLUMN link_target TEXT;
        ALTER TABLE directory_nodes ADD COLUMN scan_error_cause TEXT;
        ALTER TABLE file_nodes ADD COLUMN scan_error_cause TEXT;
        ALTER TABLE file_nodes ADD COLUMN scan_error TEXT;

        CREATE TABLE IF NOT EXISTS file_links (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id      INTEGER NOT NULL REFERENCES scan_jobs(id),
            full_path   TEXT NOT NULL,
            name        TEXT NOT NULL,
            parent_path TEXT NOT NULL,
            link_kind   TEXT NOT NULL,
            link_target TEXT,
            scanned_at  TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_file_links_job_path ON file_links(job_id, full_path);

        UPDATE directory_nodes SET scan_error_cause = CASE
                WHEN dir_reason LIKE '%denied%' THEN 'access_denied'
                WHEN dir_reason LIKE '%could not find%' OR dir_reason LIKE '%not found%' THEN 'not_found'
                WHEN dir_reason LIKE '%too long%' THEN 'path_too_long'
                ELSE 'io_error'
            END
        WHERE dir_status = 4 AND scan_error_cause IS NULL;
        """;
}
