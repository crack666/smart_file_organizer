namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 10: second check of uncertain results and retries of failed files
/// (https://github.com/crack666/smart_file_organizer/issues/23).
/// <list type="bullet">
/// <item><c>ai_results.certainty</c>: <c>certain</c>, <c>likely</c>, <c>uncertain</c> or <c>review</c>; NULL for
/// rows written before this step.</item>
/// <item><c>ai_results.attempts</c>: how often the file went through Phase 2; <c>previous_error</c>: the error
/// of the attempt before.</item>
/// <item><c>result_checks</c>: one row per check with the reasons, the outcome and both answers as JSON.
/// Rows are only added.</item>
/// </list>
/// </summary>
internal sealed class M0010_ResultChecks : SqlSchemaMigration
{
    public override int Version => 10;
    public override string Description => "Certainty, attempts and previous error on ai_results; result_checks";

    protected override string Sql => """
        ALTER TABLE ai_results ADD COLUMN certainty TEXT;
        ALTER TABLE ai_results ADD COLUMN attempts INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE ai_results ADD COLUMN previous_error TEXT;
        CREATE TABLE result_checks (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            file_node_id  INTEGER NOT NULL REFERENCES file_nodes(id) ON DELETE CASCADE,
            reasons       TEXT    NOT NULL,
            outcome       TEXT    NOT NULL,
            rule_note     TEXT,
            first_answer  TEXT    NOT NULL,
            second_answer TEXT,
            checked_at    TEXT    NOT NULL
        );
        CREATE INDEX idx_result_checks_file ON result_checks(file_node_id);
        """;
}
