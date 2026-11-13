namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 22: descriptions of library folders (https://github.com/crack666/smart_file_organizer/issues/56).
/// <c>sfo library match</c> describes a candidate folder from a small sample of its own files (scene, occasion,
/// document kind, issuer, as stored in <c>file_facts</c> for a source) and keeps the description here, so a folder is
/// sampled once and not on every match. <c>source</c> says where the description comes from: <c>sample</c> today,
/// <c>external</c> reserved for descriptions another program already holds. The library is only read; this table is
/// the only thing written about it. Rows go with the job.
/// </summary>
internal sealed class M0022_FolderDescriptions : SqlSchemaMigration
{
    public override int Version => 22;
    public override string Description => "Descriptions of library folders from a sample of their files";

    protected override string Sql => """
        CREATE TABLE folder_descriptions (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id        INTEGER NOT NULL REFERENCES scan_jobs(id),
            relative_path TEXT    NOT NULL,
            source        TEXT    NOT NULL CHECK (source IN ('sample', 'external')),
            sample_json   TEXT    NOT NULL,
            facts_json    TEXT    NOT NULL,
            sampled       INTEGER NOT NULL,
            described     INTEGER NOT NULL,
            model         TEXT,
            described_at  TEXT    NOT NULL,
            UNIQUE (job_id, relative_path, source)
        );
        """;
}
