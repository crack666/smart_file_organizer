namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 9: test cases from corrections and stored evaluation runs
/// (https://github.com/crack666/smart_file_organizer/issues/36).
/// <c>eval_cases</c> keeps every correction of the human with the evidence the model saw, so a
/// later change can be checked against it; for sensitive files only path and markers.
/// <c>eval_runs</c> keeps each run with model, prompt fingerprint and settings, so two runs can
/// be compared. Both tables stay in the local database.
/// </summary>
internal sealed class M0009_Evaluation : SqlSchemaMigration
{
    public override int Version => 9;
    public override string Description => "Evaluation: test cases from corrections, stored runs";

    protected override string Sql => """
        CREATE TABLE eval_cases (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            kind              TEXT    NOT NULL,
            origin            TEXT    NOT NULL,
            job_id            INTEGER,
            file_node_id      INTEGER,
            subject_path      TEXT    NOT NULL,
            relative_path     TEXT,
            expected          TEXT    NOT NULL,
            observed          TEXT,
            observed_model    TEXT,
            sensitive_markers TEXT,
            evidence_json     TEXT,
            created_at        TEXT    NOT NULL,
            updated_at        TEXT    NOT NULL,
            UNIQUE (kind, job_id, subject_path)
        );

        CREATE TABLE eval_runs (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            mode               TEXT    NOT NULL,
            started_at         TEXT    NOT NULL,
            finished_at        TEXT    NOT NULL,
            subject            TEXT    NOT NULL,
            job_id             INTEGER,
            model              TEXT,
            stub               TEXT,
            prompt_fingerprint TEXT,
            app_version        TEXT,
            settings_json      TEXT    NOT NULL,
            report_json        TEXT    NOT NULL
        );

        CREATE INDEX ix_eval_runs_mode ON eval_runs (mode, id);
        """;
}
