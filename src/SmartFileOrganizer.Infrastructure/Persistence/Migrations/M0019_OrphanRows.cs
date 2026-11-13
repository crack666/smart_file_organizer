namespace SmartFileOrganizer.Infrastructure.Persistence.Migrations;

/// <summary>
/// Step 19: removes rows whose parent no longer exists (https://github.com/crack666/smart_file_organizer/issues/75).
/// Until this step foreign keys were never enforced (the pragma only reached the connection that ran the migrations),
/// and deleting a job left rows behind in <c>directory_verdicts</c>, <c>directory_samples</c>, <c>file_facts</c>,
/// <c>result_checks</c> and others. From now on every connection enforces foreign keys, so these rows have to go
/// first. Children are removed before their parents' orphans, so a row whose parent goes in this step goes too.
/// Optional references (<c>unit_directory_node_id</c>, <c>match_file_node_id</c>, ...) are set to NULL instead.
/// Test cases and evaluation runs (<c>eval_cases</c>, <c>eval_runs</c>) are kept and only lose their job and file;
/// <c>content_fingerprints</c> belongs to paths, not jobs, and stays.
/// </summary>
internal sealed class M0019_OrphanRows : SqlSchemaMigration
{
    public override int Version => 19;
    public override string Description => "Remove rows left behind by deleted jobs";

    protected override string Sql => """
        DELETE FROM directory_nodes WHERE job_id NOT IN (SELECT id FROM scan_jobs);
        DELETE FROM file_nodes      WHERE job_id NOT IN (SELECT id FROM scan_jobs);
        DELETE FROM file_links      WHERE job_id NOT IN (SELECT id FROM scan_jobs);
        DELETE FROM comparisons     WHERE source_job_id NOT IN (SELECT id FROM scan_jobs)
                                       OR (against_job_id IS NOT NULL AND against_job_id NOT IN (SELECT id FROM scan_jobs));
        DELETE FROM bundles         WHERE job_id NOT IN (SELECT id FROM scan_jobs);
        DELETE FROM bundle_builds   WHERE job_id NOT IN (SELECT id FROM scan_jobs);
        DELETE FROM finding_decisions  WHERE job_id NOT IN (SELECT id FROM scan_jobs);
        DELETE FROM model_server_waits WHERE job_id NOT IN (SELECT id FROM scan_jobs);
        DELETE FROM library_advice     WHERE job_id NOT IN (SELECT id FROM scan_jobs);

        DELETE FROM ai_results       WHERE file_node_id NOT IN (SELECT id FROM file_nodes);
        UPDATE ai_results SET inherited_from_directory_node_id = NULL
            WHERE inherited_from_directory_node_id IS NOT NULL
              AND inherited_from_directory_node_id NOT IN (SELECT id FROM directory_nodes);
        DELETE FROM user_overrides   WHERE file_node_id NOT IN (SELECT id FROM file_nodes);
        DELETE FROM file_facts       WHERE file_node_id NOT IN (SELECT id FROM file_nodes);
        DELETE FROM result_checks    WHERE file_node_id NOT IN (SELECT id FROM file_nodes);
        DELETE FROM ai_directory_results WHERE directory_node_id NOT IN (SELECT id FROM directory_nodes);
        DELETE FROM directory_samples    WHERE directory_node_id NOT IN (SELECT id FROM directory_nodes);
        DELETE FROM directory_verdicts   WHERE directory_node_id NOT IN (SELECT id FROM directory_nodes);
        UPDATE directory_verdicts SET unit_directory_node_id = NULL
            WHERE unit_directory_node_id IS NOT NULL
              AND unit_directory_node_id NOT IN (SELECT id FROM directory_nodes);

        DELETE FROM comparison_files WHERE comparison_id NOT IN (SELECT id FROM comparisons)
                                        OR file_node_id NOT IN (SELECT id FROM file_nodes);
        UPDATE comparison_files SET match_file_node_id = NULL
            WHERE match_file_node_id IS NOT NULL AND match_file_node_id NOT IN (SELECT id FROM file_nodes);

        UPDATE bundles SET parent_bundle_id = NULL
            WHERE parent_bundle_id IS NOT NULL AND parent_bundle_id NOT IN (SELECT id FROM bundles);
        DELETE FROM bundle_files WHERE bundle_id NOT IN (SELECT id FROM bundles)
                                    OR file_node_id NOT IN (SELECT id FROM file_nodes);

        UPDATE eval_cases SET job_id = NULL
            WHERE job_id IS NOT NULL AND job_id NOT IN (SELECT id FROM scan_jobs);
        UPDATE eval_cases SET file_node_id = NULL
            WHERE file_node_id IS NOT NULL AND file_node_id NOT IN (SELECT id FROM file_nodes);
        UPDATE eval_runs SET job_id = NULL
            WHERE job_id IS NOT NULL AND job_id NOT IN (SELECT id FROM scan_jobs);
        """;
}
