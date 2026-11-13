namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 20: the decisions of stage 2 (https://github.com/crack666/smart_file_organizer/issues/37). Stage 1 describes
/// a file once (results and <c>file_facts</c>, which stay as they are, so earlier analyses stay readable and can be
/// decided without a new model call); <c>sfo decide</c> computes worth and target from them by rule and replaces the
/// rows of the job. <c>worth</c> is a retention code (<c>keep</c>, <c>disposable</c>) or null for no statement;
/// <c>basis</c> and <c>target_basis</c> are JSON lists of the inputs (<c>key</c>, <c>value</c>, <c>source</c>).
/// Rows go with their file (a rescan of its folder) and are written anew by the next <c>sfo decide</c>.
/// </summary>
internal sealed class M0020_FileDecisions : SqlSchemaMigration
{
    public override int Version => 20;
    public override string Description => "Stage 2: decisions per file from facts and rules";

    protected override string Sql => """
        CREATE TABLE file_decisions (
            file_node_id      INTEGER PRIMARY KEY REFERENCES file_nodes(id) ON DELETE CASCADE,
            job_id            INTEGER NOT NULL REFERENCES scan_jobs(id),
            worth             TEXT CHECK (worth IN ('keep', 'disposable')),
            certainty         TEXT NOT NULL CHECK (certainty IN ('certain', 'likely', 'uncertain', 'review')),
            rule              TEXT NOT NULL,
            basis             TEXT NOT NULL,
            target_id         TEXT,
            target_path       TEXT,
            target_rule       TEXT,
            target_basis      TEXT,
            target_unresolved TEXT,
            decided_at        TEXT NOT NULL
        );
        CREATE INDEX idx_file_decisions_job ON file_decisions(job_id);
        """;
}
