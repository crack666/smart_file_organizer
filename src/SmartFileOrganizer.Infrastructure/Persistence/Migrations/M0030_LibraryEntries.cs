namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 30: <c>library_entries</c>, what an executed plan wrote into the stored holding of a library
/// (https://github.com/crack666/smart_file_organizer/issues/86). A checked copy of step 1 becomes a file of the library
/// job (<c>file_node_id</c>, added by the plan when <c>node_added</c> is 1) and the source file counts as
/// <c>backed_up</c> in the newest comparison of the source against that library (<c>comparison_id</c>,
/// <c>source_file_node_id</c>). The row keeps what the comparison said before (<c>had_row</c> and the <c>prev_</c>
/// columns), so the undo puts it back; <c>undone_at</c> marks a row the undo took back, and a file the plan added is
/// then removed from the library job again (<c>file_node_id</c> null). Rows go with the plan and with either job.
/// </summary>
internal sealed class M0030_LibraryEntries : SqlSchemaMigration
{
    public override int Version => 30;
    public override string Description => "Library holding written forward after a plan run";

    protected override string Sql => """
        CREATE TABLE library_entries (
            id                       INTEGER PRIMARY KEY AUTOINCREMENT,
            plan_id                  INTEGER NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
            action_id                INTEGER NOT NULL,
            step_id                  INTEGER NOT NULL,
            library_job_id           INTEGER NOT NULL REFERENCES scan_jobs(id),
            target_path              TEXT    NOT NULL,
            file_node_id             INTEGER REFERENCES file_nodes(id),
            node_added               INTEGER NOT NULL,
            comparison_id            INTEGER REFERENCES comparisons(id),
            source_file_node_id      INTEGER REFERENCES file_nodes(id),
            had_row                  INTEGER NOT NULL DEFAULT 0,
            prev_state               TEXT,
            prev_match_file_node_id  INTEGER,
            prev_match_count         INTEGER NOT NULL DEFAULT 0,
            prev_error               TEXT,
            prev_match_hash          TEXT,
            prev_match_verified_at   TEXT,
            recorded_at              TEXT    NOT NULL,
            undone_at                TEXT
        );
        CREATE INDEX idx_library_entries_plan ON library_entries(plan_id, action_id);
        CREATE INDEX idx_library_entries_node ON library_entries(file_node_id);
        """;
}
