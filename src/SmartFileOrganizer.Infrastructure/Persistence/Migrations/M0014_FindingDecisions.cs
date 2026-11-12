namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 14: decisions about findings (https://github.com/crack666/smart_file_organizer/issues/26).
/// Findings are computed from scan, analysis, comparison and bundles each time they are read; what a person or a
/// steering agent decided about one is the only state, keyed by the finding's stable key. <c>state</c> is
/// <c>accepted</c>, <c>rejected</c> or <c>wrong</c> (no row: open); <c>origin</c> is <c>human</c> or <c>agent</c>;
/// <c>snapshot_json</c> keeps the finding as it was listed when decided. A rescan keeps the decisions, because
/// keys are built from paths, not from row ids; deleting the job drops them.
/// </summary>
internal sealed class M0014_FindingDecisions : SqlSchemaMigration
{
    public override int Version => 14;
    public override string Description => "Findings: decisions per job and finding key";

    protected override string Sql => """
        CREATE TABLE finding_decisions (
            job_id          INTEGER NOT NULL REFERENCES scan_jobs(id),
            finding_key     TEXT    NOT NULL,
            kind            TEXT    NOT NULL
                CHECK (kind IN ('not_backed_up', 'in_noise', 'misplaced', 'sensitive', 'duplicate')),
            state           TEXT    NOT NULL CHECK (state IN ('accepted', 'rejected', 'wrong')),
            origin          TEXT    NOT NULL CHECK (origin IN ('human', 'agent')),
            note            TEXT,
            decided_at      TEXT    NOT NULL,
            snapshot_json   TEXT    NOT NULL,
            PRIMARY KEY (job_id, finding_key)
        );
        """;
}
