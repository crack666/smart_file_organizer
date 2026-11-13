namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 19: <c>comparisons.run_id</c> (https://github.com/crack666/smart_file_organizer/issues/74). A library often has
/// several roots (separate shares for photos, videos, documents), so <c>sfo compare</c> takes several library jobs. Each
/// pair of source and library is still stored on its own, as before; the comparisons of one run share the id of the first
/// of them, so later steps know which libraries were named together. A comparison stored before has its own id as run:
/// it stays readable as a run with one library.
/// </summary>
internal sealed class M0019_ComparisonRuns : SqlSchemaMigration
{
    public override int Version => 19;
    public override string Description => "Comparisons of one run with several libraries";

    protected override string Sql => """
        ALTER TABLE comparisons ADD COLUMN run_id INTEGER;
        UPDATE comparisons SET run_id = id WHERE run_id IS NULL;
        CREATE INDEX IF NOT EXISTS idx_comparisons_run ON comparisons(source_job_id, run_id);
        """;
}
