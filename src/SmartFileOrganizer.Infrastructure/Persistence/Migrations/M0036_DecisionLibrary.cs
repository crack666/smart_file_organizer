namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 36: the library a person chose for the targets of <c>sfo decide --targets</c>, by the root of the structure or
/// <c>--library</c> (https://github.com/crack666/smart_file_organizer/issues/71). <c>target_path</c> is then relative to the
/// root of that library; before it was only relative to the target structure, and the plan had to be told where that is.
/// Rows stored before keep <c>NULL</c> (none chosen) until the next <c>sfo decide</c>.
/// </summary>
internal sealed class M0036_DecisionLibrary : SqlSchemaMigration
{
    public override int Version => 36;
    public override string Description => "Library of the targets of stage 2";

    protected override string Sql => """
        ALTER TABLE file_decisions ADD COLUMN target_library_job_id INTEGER REFERENCES scan_jobs(id) ON DELETE SET NULL;
        """;
}
