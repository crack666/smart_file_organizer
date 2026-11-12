namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 5: staged analysis (https://github.com/crack666/smart_file_organizer/issues/8).
/// <list type="bullet">
/// <item><c>directory_samples</c>: the sample of a uniform directory, its members and its outcome.</item>
/// <item><c>directory_verdicts.escalated_from</c> / <c>escalation_reason</c>: a directory whose sample
/// contradicted the uniform verdict is raised to mixed; the old verdict and the reason stay visible.
/// No CHECK on <c>escalated_from</c>: it holds a verdict code, and those codes are switched centrally.</item>
/// <item><c>ai_results.origin</c> (<c>rule</c>, <c>model</c>, <c>inherited</c>, <c>human</c>, <c>agent</c>,
/// <c>external</c>) and <c>inherited_from_directory_node_id</c>: an inherited result is recognisable as such.</item>
/// <item><c>ai_results.keep_hint</c> (<c>temporary</c>, <c>worth_keeping</c>) and <c>keep_reason</c>:
/// the judgement of the model for files in mixed directories.</item>
/// </list>
/// New codes are English. Existing rows keep <c>NULL</c>: results written before this step were all judged
/// by the model one by one.
/// </summary>
internal sealed class M0005_DirectorySamples : SqlSchemaMigration
{
    public override int Version => 5;
    public override string Description => "Directory samples, escalation and derivation of file results";

    protected override string Sql => """
        CREATE TABLE directory_samples (
            id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            directory_node_id     INTEGER NOT NULL UNIQUE REFERENCES directory_nodes(id),
            seed                  TEXT NOT NULL,
            state                 TEXT NOT NULL CHECK (state IN ('open', 'confirmed', 'escalated')),
            eligible_count        INTEGER NOT NULL,
            members_json          TEXT NOT NULL,
            max_deviation_share   REAL NOT NULL,
            majority_category     TEXT,
            deviating_count       INTEGER NOT NULL DEFAULT 0,
            failed_count          INTEGER NOT NULL DEFAULT 0,
            deviation_share       REAL,
            inherited_count       INTEGER NOT NULL DEFAULT 0,
            inherited_confidence  REAL,
            reason                TEXT,
            drawn_at              TEXT NOT NULL,
            decided_at            TEXT
        );

        ALTER TABLE directory_verdicts ADD COLUMN escalated_from TEXT;
        ALTER TABLE directory_verdicts ADD COLUMN escalation_reason TEXT;

        ALTER TABLE ai_results ADD COLUMN origin TEXT
            CHECK (origin IN ('rule', 'model', 'inherited', 'human', 'agent', 'external'));
        ALTER TABLE ai_results ADD COLUMN inherited_from_directory_node_id INTEGER REFERENCES directory_nodes(id);
        ALTER TABLE ai_results ADD COLUMN keep_hint TEXT
            CHECK (keep_hint IN ('temporary', 'worth_keeping'));
        ALTER TABLE ai_results ADD COLUMN keep_reason TEXT;
        """;
}
