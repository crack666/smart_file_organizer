using System.Runtime.CompilerServices;
using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>Read-only queries for the CLI read commands. Never writes.</summary>
public class JobReadRepository : IJobReadRepository
{
    private const string FileSelect = """
        SELECT f.id, f.job_id, f.full_path, f.name, f.parent_path, f.root_path, f.relative_path,
               f.relative_dir, f.depth, f.size, f.last_write_time, f.extension, f.file_type, f.status,
               f.scanned_at, f.sensitive_markers, f.sensitive_rule,
               a.id AS ai_id, a.category AS ai_category, a.importance AS ai_importance,
               a.confidence AS ai_confidence, a.summary AS ai_summary,
               a.suggested_target AS ai_suggested_target, a.model_used AS ai_model_used,
               a.analyzed_at AS ai_analyzed_at, {0} AS ai_raw_response, a.error AS ai_error,
               a.is_ai_result AS ai_is_ai_result, a.sensitive_markers AS ai_sensitive_markers,
               a.certainty AS ai_certainty, a.attempts AS ai_attempts, a.previous_error AS ai_previous_error,
               a.origin AS ai_origin, a.keep_hint AS ai_keep_hint,
               o.id AS ovr_id, o.overridden_category AS ovr_category, o.overridden_target AS ovr_target,
               o.note AS ovr_note, o.overridden_at AS ovr_at,
               CASE WHEN dv.id IS NULL THEN NULL WHEN dv.verdict IS NULL THEN 'error' ELSE dv.verdict END AS dir_verdict,
               dv.retention AS dir_retention
        FROM file_nodes f
        LEFT JOIN ai_results a ON a.file_node_id = f.id
        LEFT JOIN user_overrides o ON o.file_node_id = f.id
        LEFT JOIN directory_nodes dn ON dn.job_id = f.job_id AND dn.full_path = f.parent_path
        LEFT JOIN directory_verdicts dv ON dv.directory_node_id = dn.id
        """;

    private readonly DatabaseContext _db;

    public JobReadRepository(DatabaseContext db) => _db = db;

    public Task<IReadOnlyList<ResultCheck>> GetResultChecksAsync(long fileNodeId, CancellationToken ct = default) =>
        new ClassificationRepository(_db).GetChecksAsync(fileNodeId, ct);

    public async Task<IReadOnlyList<DirectoryReadRow>> GetDirectoriesAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<DirectoryReadRow>(new CommandDefinition(
            """
            SELECT d.id, d.full_path, d.name, d.parent_path, d.relative_path, d.depth, d.dir_status,
                   d.subdir_count, d.sensitive_markers, d.sensitive_rule,
                   d.dir_reason AS scan_reason, d.link_kind, d.link_target, d.scan_error_cause,
                   dv.verdict, dv.source AS verdict_source, dv.rule AS verdict_rule, dv.retention,
                   dv.purpose, dv.reason, dv.error AS verdict_error, dv.escalated_from,
                   s.summary AS ai_summary, s.error AS ai_summary_error,
                   s.model_used AS ai_summary_model, s.marked_inputs AS ai_summary_marked_inputs
            FROM directory_nodes d
            LEFT JOIN directory_verdicts dv ON dv.directory_node_id = d.id
            LEFT JOIN ai_directory_results s ON s.directory_node_id = d.id AND s.phase = 'summary'
            WHERE d.job_id = @jobId
            ORDER BY d.full_path
            """,
            new { jobId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<DirectoryFileTotals>> GetDirectoryFileTotalsAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<DirectoryFileTotals>(new CommandDefinition(
            """
            SELECT parent_path, COUNT(*) AS files, COALESCE(SUM(size), 0) AS bytes
            FROM file_nodes
            WHERE job_id = @jobId
            GROUP BY parent_path
            """,
            new { jobId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async IAsyncEnumerable<FileReadRow> StreamFilesAsync(
        FileListFilter filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var where = new List<string> { "f.job_id = @JobId" };
        var parameters = new DynamicParameters();
        parameters.Add("JobId", filter.JobId);

        if (filter.Directory is not null)
        {
            parameters.Add("Dir", filter.Directory);
            if (filter.Recursive && filter.DirectoryPrefix is not null)
            {
                // substr instead of LIKE: paths may contain % and _.
                parameters.Add("Prefix", filter.DirectoryPrefix);
                parameters.Add("PrefixLength", filter.DirectoryPrefix.Length);
                where.Add("(f.parent_path = @Dir OR substr(f.parent_path, 1, @PrefixLength) = @Prefix)");
            }
            else
            {
                where.Add("f.parent_path = @Dir");
            }
        }

        if (filter.BundleIds.Count > 0)
        {
            parameters.Add("BundleIds", filter.BundleIds.ToArray());
            where.Add("f.id IN (SELECT file_node_id FROM bundle_files WHERE bundle_id IN @BundleIds)");
        }

        if (filter.FileTypes.Count > 0)
        {
            parameters.Add("Types", filter.FileTypes.Select(t => (int)t).ToArray());
            where.Add("f.file_type IN @Types");
        }

        if (filter.Statuses.Count > 0)
        {
            parameters.Add("Statuses", filter.Statuses.Select(s => (int)s).ToArray());
            where.Add("f.status IN @Statuses");
        }

        var sql = string.Format(FileSelect, filter.IncludeRawResponse ? "a.raw_response" : "NULL")
                  + " WHERE " + string.Join(" AND ", where)
                  + (filter.OrderBySize ? " ORDER BY f.size DESC, f.full_path" : " ORDER BY f.full_path");

        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await foreach (var row in conn.QueryUnbufferedAsync<FileRow>(sql, parameters).WithCancellation(ct))
            yield return row.ToReadRow();
    }

    public async Task<FileReadRow?> GetFileByPathAsync(long jobId, string fullPath, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<FileRow>(new CommandDefinition(
            string.Format(FileSelect, "a.raw_response") + " WHERE f.job_id = @jobId AND f.full_path = @fullPath",
            new { jobId, fullPath }, cancellationToken: ct));
        return row?.ToReadRow();
    }

    public async Task<IReadOnlyList<DirectoryClassificationResult>> GetDirectoryAnalysesAsync(
        long directoryNodeId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<DirectoryClassificationResult>(new CommandDefinition(
            "SELECT * FROM ai_directory_results WHERE directory_node_id = @directoryNodeId ORDER BY phase",
            new { directoryNodeId }, cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Flat shape of one joined file row; markers stay strings and are parsed here.</summary>
    private sealed class FileRow
    {
        public long Id { get; set; }
        public long JobId { get; set; }
        public string FullPath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ParentPath { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string RelativeDir { get; set; } = string.Empty;
        public int Depth { get; set; }
        public long Size { get; set; }
        public DateTime LastWriteTime { get; set; }
        public string Extension { get; set; } = string.Empty;
        public int FileType { get; set; }
        public int Status { get; set; }
        public DateTime ScannedAt { get; set; }
        public string? SensitiveMarkers { get; set; }
        public string? SensitiveRule { get; set; }

        public long? AiId { get; set; }
        public int AiCategory { get; set; }
        public int AiImportance { get; set; }
        public double AiConfidence { get; set; }
        public string? AiSummary { get; set; }
        public string? AiSuggestedTarget { get; set; }
        public string? AiModelUsed { get; set; }
        public DateTime? AiAnalyzedAt { get; set; }
        public string? AiRawResponse { get; set; }
        public string? AiError { get; set; }
        public bool AiIsAiResult { get; set; }
        public string? AiSensitiveMarkers { get; set; }
        public string? AiCertainty { get; set; }
        public int? AiAttempts { get; set; }
        public string? AiPreviousError { get; set; }
        public string? AiOrigin { get; set; }
        public string? AiKeepHint { get; set; }

        public long? OvrId { get; set; }
        public int OvrCategory { get; set; }
        public string? OvrTarget { get; set; }
        public string? OvrNote { get; set; }
        public DateTime? OvrAt { get; set; }

        public string? DirVerdict { get; set; }
        public string? DirRetention { get; set; }

        public FileReadRow ToReadRow()
        {
            var file = new FileNode
            {
                Id = Id,
                JobId = JobId,
                FullPath = FullPath,
                Name = Name,
                ParentPath = ParentPath,
                RootPath = RootPath,
                RelativePath = RelativePath,
                RelativeDir = RelativeDir,
                Depth = Depth,
                Size = Size,
                LastWriteTime = LastWriteTime,
                Extension = Extension,
                FileType = (FileType)FileType,
                Status = (FileNodeStatus)Status,
                ScannedAt = ScannedAt,
                SensitiveMarkers = SensitiveMarkerNames.Parse(SensitiveMarkers),
                SensitiveRule = SensitiveRule
            };

            if (AiId is { } aiId)
            {
                file.Classification = new FileClassification
                {
                    Id = aiId,
                    FileNodeId = Id,
                    Category = (FileCategory)AiCategory,
                    Importance = (Importance)AiImportance,
                    Confidence = AiConfidence,
                    Summary = AiSummary,
                    SuggestedTarget = AiSuggestedTarget,
                    ModelUsed = AiModelUsed,
                    AnalyzedAt = AiAnalyzedAt ?? default,
                    RawResponse = AiRawResponse,
                    Error = AiError,
                    IsAiResult = AiIsAiResult,
                    SensitiveMarkers = SensitiveMarkerNames.Parse(AiSensitiveMarkers),
                    Certainty = VerdictCodes.ParseNullable<ResultCertainty>(AiCertainty),
                    Attempts = AiAttempts ?? 1,
                    PreviousError = AiPreviousError,
                    Origin = VerdictCodes.ParseNullable<ResultOrigin>(AiOrigin),
                    KeepHint = VerdictCodes.ParseNullable<KeepHint>(AiKeepHint)
                };
            }

            if (OvrId is { } ovrId)
            {
                file.Override = new UserOverride
                {
                    Id = ovrId,
                    FileNodeId = Id,
                    OverriddenCategory = (FileCategory)OvrCategory,
                    OverriddenTarget = OvrTarget,
                    Note = OvrNote,
                    OverriddenAt = OvrAt ?? default
                };
            }

            return new FileReadRow(file, DirVerdict, DirRetention);
        }
    }
}
