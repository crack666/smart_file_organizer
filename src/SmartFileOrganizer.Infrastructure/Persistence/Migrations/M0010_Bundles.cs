namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 10: bundles, the units the person decides once (https://github.com/crack666/smart_file_organizer/issues/39).
/// <list type="bullet">
/// <item><c>bundles</c>: one row per bundle, as a tree per job (<c>parent_bundle_id</c>). <c>kind</c>, <c>origin</c>,
/// <c>part</c> and <c>time_source</c> are fixed English codes; <c>facts_json</c> holds distributions, markers and
/// directories. <c>styled_name</c> stays empty until a model proposes a name in the style of the library.</item>
/// <item><c>bundle_files</c>: the bundle each file belongs to directly; a file is in at most one.</item>
/// <item><c>bundle_builds</c>: the last build per job, with its settings and the statistics with and without dumps.</item>
/// </list>
/// A rescan of a directory or deleting the job drops the bundles of the job; they are built again on request.
/// </summary>
internal sealed class M0010_Bundles : SqlSchemaMigration
{
    public override int Version => 10;
    public override string Description => "Bundles: dumps, folders and events per job";

    protected override string Sql => """
        CREATE TABLE bundles (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id                  INTEGER NOT NULL REFERENCES scan_jobs(id),
            parent_bundle_id        INTEGER REFERENCES bundles(id),
            kind                    TEXT NOT NULL
                CHECK (kind IN ('dump', 'dump_keep', 'dump_noise', 'folder', 'event', 'scattered')),
            origin                  TEXT CHECK (origin IN ('phone', 'computer', 'removable_drive', 'software')),
            part                    TEXT,
            root_path               TEXT,
            root_directory_node_id  INTEGER,
            suggested_name          TEXT NOT NULL,
            styled_name             TEXT,
            first_time              TEXT,
            last_time               TEXT,
            time_source             TEXT CHECK (time_source IN ('capture_date', 'file_name', 'modified_date', 'mixed')),
            file_count              INTEGER NOT NULL,
            total_bytes             INTEGER NOT NULL,
            directory_count         INTEGER NOT NULL,
            empty_directory_count   INTEGER NOT NULL,
            reason                  TEXT NOT NULL,
            facts_json              TEXT NOT NULL,
            built_at                TEXT NOT NULL
        );
        CREATE INDEX idx_bundles_job ON bundles(job_id, parent_bundle_id);

        CREATE TABLE bundle_files (
            file_node_id    INTEGER PRIMARY KEY REFERENCES file_nodes(id),
            bundle_id       INTEGER NOT NULL REFERENCES bundles(id)
        );
        CREATE INDEX idx_bundle_files_bundle ON bundle_files(bundle_id);

        CREATE TABLE bundle_builds (
            job_id          INTEGER PRIMARY KEY REFERENCES scan_jobs(id),
            built_at        TEXT NOT NULL,
            settings_json   TEXT NOT NULL,
            stats_json      TEXT NOT NULL
        );
        """;
}
