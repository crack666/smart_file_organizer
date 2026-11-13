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
                    WHERE n.job_id = @jobId AND r.phase = 'summary' AND r.error IS NOT NULL) AS summary_errors,
                (SELECT COUNT(*) FROM directory_nodes n
                    WHERE n.job_id = @jobId AND n.dir_status <> @skip AND n.name <> '' AND substr(n.name, 1, 1) <> '.'
                      AND NOT EXISTS (SELECT 1 FROM ai_directory_results r WHERE r.directory_node_id = n.id AND r.phase = 'summary')
                      AND n.analysis_skip IS NULL
                      AND CASE WHEN EXISTS (SELECT 1 FROM ai_directory_results r WHERE r.directory_node_id = n.id AND r.phase = 'pre_assessment')
                               THEN NOT EXISTS (SELECT 1 FROM file_nodes f WHERE f.job_id = @jobId AND f.parent_path = n.full_path)
                               ELSE EXISTS (SELECT 1 FROM directory_verdicts v
                                            WHERE v.directory_node_id = n.id AND v.verdict IS NOT NULL AND v.error IS NULL)
                          END) AS summaries_not_needed,
                (SELECT COUNT(*) FROM directory_nodes n
                    WHERE n.job_id = @jobId AND n.analysis_skip IS NOT NULL
                      AND n.dir_status <> @skip AND n.name <> '' AND substr(n.name, 1, 1) <> '.'
                      AND NOT EXISTS (SELECT 1 FROM ai_directory_results r WHERE r.directory_node_id = n.id AND r.phase = 'pre_assessment')
                      AND NOT EXISTS (SELECT 1 FROM directory_verdicts v
                                      WHERE v.directory_node_id = n.id AND v.verdict IS NOT NULL AND v.error IS NULL)) AS pre_assessment_backed_up,
                (SELECT COUNT(*) FROM directory_nodes n
                    WHERE n.job_id = @jobId AND n.analysis_skip IS NOT NULL
                      AND n.dir_status <> @skip AND n.name <> '' AND substr(n.name, 1, 1) <> '.'
                      AND NOT EXISTS (SELECT 1 FROM ai_directory_results r WHERE r.directory_node_id = n.id AND r.phase = 'summary')) AS summary_backed_up
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
                COALESCE(SUM(CASE WHEN status = @skipped AND analysis_skip IS NOT NULL THEN 1 ELSE 0 END), 0) AS backed_up,
                COALESCE(SUM(CASE WHEN status = @inherited THEN 1 ELSE 0 END), 0) AS inherited,
                COALESCE(SUM(CASE WHEN status = @deferred THEN 1 ELSE 0 END), 0) AS deferred,
                (SELECT COUNT(*) FROM ai_results r JOIN file_nodes n ON n.id = r.file_node_id
                    WHERE n.job_id = @jobId AND n.status = @analyzed AND r.error IS NULL
                      AND (r.origin = 'model' OR (r.origin IS NULL AND r.is_ai_result = 1))) AS bymodel,
                (SELECT COUNT(*) FROM ai_results r JOIN file_nodes n ON n.id = r.file_node_id
                    WHERE n.job_id = @jobId AND n.status = @analyzed AND r.origin = 'reused') AS reused
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

        // Rate of the files that really went to the model: the newest model results of the job, when the newest of
        // them is recent (a run that stopped long ago has no rate).
        double? ratePerMinute = null;
        if (files.Pending > 0)
        {
            var times = (await conn.QueryAsync<DateTime>(new CommandDefinition(
                """
                SELECT r.analyzed_at FROM ai_results r JOIN file_nodes f ON f.id = r.file_node_id
                WHERE f.job_id = @jobId AND r.is_ai_result = 1 AND r.model_used IS NOT NULL AND r.error IS NULL
                  AND (r.origin IS NULL OR r.origin <> 'reused')
                ORDER BY r.id DESC LIMIT @window
                """,
                new { jobId, window = RateWindow }, cancellationToken: ct))).Select(AsUtc).ToList();
            if (times.Count >= 2)
            {
                var newest = times.Max();
                var span = newest - times.Min();
                if (DateTime.UtcNow - newest < RateStale && span > TimeSpan.Zero)
                    ratePerMinute = (times.Count - 1) / span.TotalMinutes;
            }
        }

        // Every read error is counted; only the first few of each cause are listed, so a profile with hundreds
        // of locked folders reads as one line per cause instead of hundreds of lines.
        var scanErrors = (await conn.QueryAsync<ScanErrorRow>(
            """
            WITH errors AS (
                SELECT COALESCE(scan_error_cause, 'io_error') AS cause, full_path AS path, dir_reason AS message, 1 AS is_directory
                FROM directory_nodes WHERE job_id = @jobId AND dir_status = @error
                UNION ALL
                SELECT scan_error_cause, full_path, scan_error, 0
                FROM file_nodes WHERE job_id = @jobId AND scan_error_cause IS NOT NULL
            ),
            ranked AS (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY cause ORDER BY path) AS n,
                       SUM(is_directory) OVER (PARTITION BY cause) AS directories,
                       SUM(1 - is_directory) OVER (PARTITION BY cause) AS files
                FROM errors
            )
            SELECT cause, path, message, directories, files FROM ranked
            WHERE n <= @examples
            ORDER BY directories + files DESC, cause, n
            """,
            new { jobId, error = (int)DirectoryStatus.Error, examples = ExamplesPerGroup }))
            .GroupBy(r => r.Cause)
            .Select(g => new ScanErrorGroup(g.Key, g.First().Directories, g.First().Files,
                g.Select(r => new ScanErrorExample(r.Path, r.Message)).ToList()))
            .ToList();

        var links = (await conn.QueryAsync<LinkRow>(
            """
            WITH links AS (
                SELECT link_kind AS kind, full_path AS path, link_target AS target,
                       CASE WHEN dir_status = @link THEN 0 ELSE 1 END AS followed, 1 AS is_directory
                FROM directory_nodes WHERE job_id = @jobId AND link_kind IS NOT NULL
                UNION ALL
                SELECT link_kind, full_path, link_target, 0, 0 FROM file_links WHERE job_id = @jobId
            ),
            ranked AS (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY kind ORDER BY is_directory DESC, path) AS n,
                       SUM(CASE WHEN is_directory = 1 AND followed = 0 THEN 1 ELSE 0 END) OVER (PARTITION BY kind) AS not_followed,
                       SUM(followed) OVER (PARTITION BY kind) AS followed_count,
                       SUM(1 - is_directory) OVER (PARTITION BY kind) AS files
                FROM links
            )
            SELECT kind, path, target, followed, not_followed, followed_count, files FROM ranked
            WHERE n <= @examples
            ORDER BY kind, n
            """,
            new { jobId, link = (int)DirectoryStatus.Link, examples = ExamplesPerGroup }))
            .GroupBy(r => r.Kind)
            .Select(g => new LinkGroup(g.Key, g.First().NotFollowed, g.First().FollowedCount, g.First().Files,
                g.Select(r => new LinkExample(r.Path, r.Target, r.Followed != 0)).ToList()))
            .ToList();

        return new JobStatistics
        {
            ScanErrors = scanErrors,
            Links = links,
            DirectoriesTotal = dirs.Total,
            DirectoriesAiRelevant = dirs.AiRelevant,
            DirectoriesPreAssessed = dirs.PreAssessed,
            PreAssessmentErrors = dirs.PreAssessmentErrors,
            DirectoriesSummarized = dirs.Summarized,
            SummaryErrors = dirs.SummaryErrors,
            SummariesNotNeeded = dirs.SummariesNotNeeded,
            PreAssessmentBackedUp = dirs.PreAssessmentBackedUp,
            SummaryBackedUp = dirs.SummaryBackedUp,
            FilesAiBackedUp = files.BackedUp,
            FilesTotal = files.Total,
            FilesAiEligible = files.Eligible,
            FilesAiPending = files.Pending,
            FilesAiAnalyzed = files.Analyzed,
            FilesAiErrors = files.Errors,
            FilesAiSkipped = files.Skipped,
            FilesAiInherited = files.Inherited,
            FilesAiDeferred = files.Deferred,
            FilesAiByModel = files.ByModel,
            FilesAiReused = files.Reused,
            FilesAiRatePerMinute = ratePerMinute,
            RecentErrors = errors
        };
    }

    /// <summary>How many of the newest model results the rate rests on.</summary>
    private const int RateWindow = 50;

    /// <summary>A newest model result older than this means no run is going on.</summary>
    private static readonly TimeSpan RateStale = TimeSpan.FromMinutes(10);

    private static DateTime AsUtc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc)
    };

    private sealed class DirectoryCounts
    {
        public long Total { get; set; }
        public long AiRelevant { get; set; }
        public long PreAssessed { get; set; }
        public long PreAssessmentErrors { get; set; }
        public long Summarized { get; set; }
        public long SummaryErrors { get; set; }
        public long SummariesNotNeeded { get; set; }
        public long PreAssessmentBackedUp { get; set; }
        public long SummaryBackedUp { get; set; }
    }

    private sealed class FileCounts
    {
        public long Total { get; set; }
        public long Eligible { get; set; }
        public long Pending { get; set; }
        public long Analyzed { get; set; }
        public long Errors { get; set; }
        public long Skipped { get; set; }
        public long BackedUp { get; set; }
        public long Inherited { get; set; }
        public long Deferred { get; set; }
        public long ByModel { get; set; }
        public long Reused { get; set; }
    }

    /// <summary>Paths listed per error cause and per link kind in <c>sfo status</c>.</summary>
    public const int ExamplesPerGroup = 3;

    private sealed class ScanErrorRow
    {
        public string Cause { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string? Message { get; set; }
        public long Directories { get; set; }
        public long Files { get; set; }
    }

    private sealed class LinkRow
    {
        public string Kind { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string? Target { get; set; }
        public long Followed { get; set; }
        public long NotFollowed { get; set; }
        public long FollowedCount { get; set; }
        public long Files { get; set; }
    }

    private sealed class ErrorRow
    {
        public string Phase { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
