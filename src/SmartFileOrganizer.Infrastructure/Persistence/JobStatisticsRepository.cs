using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

public class JobStatisticsRepository : IJobStatisticsRepository
{
    private readonly DatabaseContext _db;

    public JobStatisticsRepository(DatabaseContext db) => _db = db;

    public async Task<JobStatistics> GetAsync(long jobId, int maxErrors, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);

        // The AI-relevant filter mirrors DirectoryPreAssessmentService and DirectorySummaryService.
        // Pre-assessed: a pre-assessment row, or a verdict without error. A folder inside a unit gets its
        // verdict from the unit and no row of its own, and is judged all the same.
        var dirs = await conn.QuerySingleAsync<DirectoryCounts>(
            """
            SELECT
                COUNT(*) AS total,
                COALESCE(SUM(CASE WHEN d.dir_status <> @skip AND d.name <> '' AND substr(d.name, 1, 1) <> '.' THEN 1 ELSE 0 END), 0) AS ai_relevant,
                (SELECT COUNT(*) FROM directory_nodes n
                    WHERE n.job_id = @jobId
                      AND (EXISTS (SELECT 1 FROM ai_directory_results r WHERE r.directory_node_id = n.id AND r.phase = 'pre_assessment')
                           OR (n.dir_status <> @skip AND n.name <> '' AND substr(n.name, 1, 1) <> '.'
                               AND EXISTS (SELECT 1 FROM directory_verdicts v
                                           WHERE v.directory_node_id = n.id AND v.verdict IS NOT NULL AND v.error IS NULL)))) AS pre_assessed,
                (SELECT COUNT(*) FROM ai_directory_results r JOIN directory_nodes n ON n.id = r.directory_node_id
                    WHERE n.job_id = @jobId AND r.phase = 'pre_assessment' AND r.error IS NOT NULL) AS pre_assessment_errors,
                (SELECT COUNT(*) FROM ai_directory_results r JOIN directory_nodes n ON n.id = r.directory_node_id
                    WHERE n.job_id = @jobId AND r.phase = 'summary') AS summarized,
                (SELECT COUNT(*) FROM ai_directory_results r JOIN directory_nodes n ON n.id = r.directory_node_id
                    WHERE n.job_id = @jobId AND r.phase = 'summary' AND r.error IS NOT NULL) AS summary_errors
            FROM directory_nodes d
            WHERE d.job_id = @jobId
            """,
            new { jobId, skip = (int)DirectoryStatus.Skip });

        var files = await conn.QuerySingleAsync<FileCounts>(
            """
            SELECT
                COUNT(*) AS total,
                COUNT(*) AS eligible,
                COALESCE(SUM(CASE WHEN status IN (@discovered, @processing) THEN 1 ELSE 0 END), 0) AS pending,
                COALESCE(SUM(CASE WHEN status = @analyzed THEN 1 ELSE 0 END), 0) AS analyzed,
                COALESCE(SUM(CASE WHEN status = @error THEN 1 ELSE 0 END), 0) AS errors,
                COALESCE(SUM(CASE WHEN status = @skipped THEN 1 ELSE 0 END), 0) AS skipped,
                COALESCE(SUM(CASE WHEN status = @inherited THEN 1 ELSE 0 END), 0) AS inherited,
                COALESCE(SUM(CASE WHEN status = @deferred THEN 1 ELSE 0 END), 0) AS deferred
            FROM file_nodes
            WHERE job_id = @jobId
            """,
            new
            {
                jobId,
                discovered = (int)FileNodeStatus.Discovered,
                processing = (int)FileNodeStatus.Processing,
                analyzed = (int)FileNodeStatus.AiAnalyzed,
                error = (int)FileNodeStatus.Error,
                skipped = (int)FileNodeStatus.Skipped,
                inherited = (int)FileNodeStatus.Inherited,
                deferred = (int)FileNodeStatus.Deferred
            });

        var errors = maxErrors <= 0
            ? []
            : (await conn.QueryAsync<ErrorRow>(
                """
                SELECT phase, path, message FROM (
                    SELECT 'classification' AS phase, f.full_path AS path, a.error AS message, a.analyzed_at AS at
                    FROM ai_results a JOIN file_nodes f ON f.id = a.file_node_id
                    WHERE f.job_id = @jobId AND a.error IS NOT NULL
                    UNION ALL
                    SELECT r.phase, n.full_path, r.error, r.analyzed_at
                    FROM ai_directory_results r JOIN directory_nodes n ON n.id = r.directory_node_id
                    WHERE n.job_id = @jobId AND r.error IS NOT NULL
                )
                ORDER BY at DESC, path
                LIMIT @maxErrors
                """,
                new { jobId, maxErrors })).Select(e => new JobErrorEntry(e.Phase, e.Path, e.Message)).ToList();

        return new JobStatistics
        {
            DirectoriesTotal = dirs.Total,
            DirectoriesAiRelevant = dirs.AiRelevant,
            DirectoriesPreAssessed = dirs.PreAssessed,
            PreAssessmentErrors = dirs.PreAssessmentErrors,
            DirectoriesSummarized = dirs.Summarized,
            SummaryErrors = dirs.SummaryErrors,
            FilesTotal = files.Total,
            FilesAiEligible = files.Eligible,
            FilesAiPending = files.Pending,
            FilesAiAnalyzed = files.Analyzed,
            FilesAiErrors = files.Errors,
            FilesAiSkipped = files.Skipped,
            FilesAiInherited = files.Inherited,
            FilesAiDeferred = files.Deferred,
            RecentErrors = errors
        };
    }

    private sealed class DirectoryCounts
    {
        public long Total { get; set; }
        public long AiRelevant { get; set; }
        public long PreAssessed { get; set; }
        public long PreAssessmentErrors { get; set; }
        public long Summarized { get; set; }
        public long SummaryErrors { get; set; }
    }

    private sealed class FileCounts
    {
        public long Total { get; set; }
        public long Eligible { get; set; }
        public long Pending { get; set; }
        public long Analyzed { get; set; }
        public long Errors { get; set; }
        public long Skipped { get; set; }
        public long Inherited { get; set; }
        public long Deferred { get; set; }
    }

    private sealed class ErrorRow
    {
        public string Phase { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
