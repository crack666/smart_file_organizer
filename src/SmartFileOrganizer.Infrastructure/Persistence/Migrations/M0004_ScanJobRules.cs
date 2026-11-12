namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 4: the scan rules (profile) a job is scanned with, stored at the job
/// (https://github.com/crack666/smart_file_organizer/issues/21). Jobs from before this step have
/// NULL here; the scan engine records the shipped rules on their next run and re-evaluates their
/// skipped and shallow folders, which were decided by the older hard-coded rules.
/// </summary>
internal sealed class M0004_ScanJobRules : SqlSchemaMigration
{
    public override int Version => 4;
    public override string Description => "Scan rules per job (name, source, hash, rules JSON)";

    protected override string Sql => """
        ALTER TABLE scan_jobs ADD COLUMN rules_name   TEXT;
        ALTER TABLE scan_jobs ADD COLUMN rules_source TEXT;
        ALTER TABLE scan_jobs ADD COLUMN rules_hash   TEXT;
        ALTER TABLE scan_jobs ADD COLUMN rules_json   TEXT;
        """;
}
