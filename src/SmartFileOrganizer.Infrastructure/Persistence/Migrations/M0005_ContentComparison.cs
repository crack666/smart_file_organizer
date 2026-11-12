namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 5: comparison of a source medium with the user's library
/// (https://github.com/crack666/smart_file_organizer/issues/24).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>scan_jobs.role</c>: <c>source</c> or <c>library</c>; existing jobs become <c>source</c>.</item>
/// <item><c>content_fingerprints</c>: checksums per path, valid while size and modification time match.
/// Keyed by path rather than job so that a rescan of a large library does not read it again.</item>
/// <item><c>comparisons</c> / <c>comparison_files</c>: the latest result per source job and library job.</item>
/// </list>
/// </remarks>
internal sealed class M0005_ContentComparison : SqlSchemaMigration
{
    public override int Version => 5;
    public override string Description => "Scan roles, content fingerprints and comparison results";

    protected override string Sql => """
        ALTER TABLE scan_jobs ADD COLUMN role TEXT NOT NULL DEFAULT 'source';

        CREATE INDEX IF NOT EXISTS idx_file_nodes_job_size
            ON file_nodes(job_id, size);
        CREATE INDEX IF NOT EXISTS idx_file_nodes_job_name
            ON file_nodes(job_id, name COLLATE NOCASE);

        CREATE TABLE IF NOT EXISTS content_fingerprints (
            full_path           TEXT PRIMARY KEY,
            size                INTEGER NOT NULL,
            last_write_ticks    INTEGER NOT NULL,
            head_tail_hash      TEXT,
            full_hash           TEXT,
            computed_at         TEXT NOT NULL,
            full_hash_at        TEXT
        );

        CREATE TABLE IF NOT EXISTS comparisons (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            source_job_id       INTEGER NOT NULL REFERENCES scan_jobs(id),
            against_job_id      INTEGER REFERENCES scan_jobs(id),
            compared_at         TEXT NOT NULL,
            complete            INTEGER NOT NULL,
            error_count         INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_comparisons_jobs
            ON comparisons(source_job_id, against_job_id);

        CREATE TABLE IF NOT EXISTS comparison_files (
            comparison_id       INTEGER NOT NULL REFERENCES comparisons(id),
            file_node_id        INTEGER NOT NULL REFERENCES file_nodes(id),
            state               TEXT,
            match_file_node_id  INTEGER REFERENCES file_nodes(id),
            match_count         INTEGER NOT NULL DEFAULT 0,
            duplicate_group     INTEGER,
            error               TEXT,
            match_hash          TEXT,
            match_verified_at   TEXT,
            PRIMARY KEY (comparison_id, file_node_id)
        );
        """;
}
