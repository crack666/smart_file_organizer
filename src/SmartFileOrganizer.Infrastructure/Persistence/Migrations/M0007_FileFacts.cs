namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 7: facts per file and the evidence notes of a result
/// (https://github.com/crack666/smart_file_organizer/issues/22).
/// <list type="bullet">
/// <item><c>file_facts</c>: what Phase 2 read from a file without the model (capture date, device,
/// page count, tags, archive listing summary, ...). <c>fact</c> and <c>source</c> are fixed English
/// codes (<c>taken_at</c>, <c>metadata</c>). One row per file and fact; a new analysis replaces them.</item>
/// <item><c>ai_results.evidence</c>: JSON with what the evidence contained and what was missing.</item>
/// </list>
/// Existing results get no facts here; they appear when a file is analysed again.
/// </summary>
internal sealed class M0007_FileFacts : SqlSchemaMigration
{
    public override int Version => 7;
    public override string Description => "File facts from metadata and rules; evidence notes on ai_results";

    protected override string Sql => """
        CREATE TABLE file_facts (
            file_node_id INTEGER NOT NULL REFERENCES file_nodes(id) ON DELETE CASCADE,
            fact         TEXT    NOT NULL,
            value        TEXT    NOT NULL,
            source       TEXT    NOT NULL,
            recorded_at  TEXT    NOT NULL,
            PRIMARY KEY (file_node_id, fact)
        );
        CREATE INDEX idx_file_facts_fact ON file_facts(fact, value);
        ALTER TABLE ai_results ADD COLUMN evidence TEXT;
        """;
}
