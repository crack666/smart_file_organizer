namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 17: <c>model_server_waits</c>, one row per job whose analysis waits for the model server right now
/// (https://github.com/crack666/smart_file_organizer/issues/63). <c>sfo status</c> runs in another process than
/// <c>sfo analyze</c> and reads it there. <c>checked_at</c> moves with every probe, so a row whose run was killed
/// shows by its age. The causes are written out here because this step must keep doing the same thing when the
/// codes change later.
/// </summary>
internal sealed class M0017_ModelServerWaits : SqlSchemaMigration
{
    public override int Version => 17;
    public override string Description => "Model server waits";

    protected override string Sql => """
        CREATE TABLE model_server_waits (
            job_id      INTEGER PRIMARY KEY REFERENCES scan_jobs(id),
            since       TEXT NOT NULL,
            checked_at  TEXT NOT NULL,
            cause       TEXT NOT NULL CHECK (cause IN ('connection_failed', 'timed_out', 'busy', 'bad_gateway'))
        );
        """;
}
