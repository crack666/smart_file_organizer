using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>Stores bundles in <c>bundles</c>, <c>bundle_files</c> and <c>bundle_builds</c> with their English codes.</summary>
public class BundleRepository : IBundleRepository
{
    private const string LocalTimeFormat = "yyyy-MM-dd'T'HH:mm:ss";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null
    };

    /// <summary>Removes every bundle of a job; used by rescans and job deletion, which change file ids.</summary>
    internal const string DeleteForJobSql = """
        DELETE FROM bundle_files WHERE bundle_id IN (SELECT id FROM bundles WHERE job_id = @jobId);
        DELETE FROM bundles WHERE job_id = @jobId;
        DELETE FROM bundle_builds WHERE job_id = @jobId;
        """;

    private readonly DatabaseContext _db;

    public BundleRepository(DatabaseContext db) => _db = db;

    public async Task ReplaceAsync(long jobId, IReadOnlyList<Bundle> roots, BundleBuild build, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(DeleteForJobSql, new { jobId }, tx);

        var stack = new Stack<(Bundle Bundle, long? ParentId)>();
        for (var i = roots.Count - 1; i >= 0; i--)
            stack.Push((roots[i], null));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (bundle, parentId) = stack.Pop();
            bundle.JobId = jobId;
            bundle.ParentId = parentId;
            bundle.BuiltAt = build.BuiltAt;
            bundle.Id = await InsertAsync(conn, (SqliteTransaction)tx, bundle);

            if (bundle.FileIds.Count > 0)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO bundle_files (file_node_id, bundle_id) VALUES (@FileId, @BundleId)",
                    bundle.FileIds.Select(id => new { FileId = id, BundleId = bundle.Id }), tx);
            }

            for (var i = bundle.Children.Count - 1; i >= 0; i--)
                stack.Push((bundle.Children[i], bundle.Id));
        }

        await conn.ExecuteAsync(
            """
            INSERT INTO bundle_builds (job_id, built_at, settings_json, stats_json)
            VALUES (@JobId, @BuiltAt, @SettingsJson, @StatsJson)
            """,
            new { JobId = jobId, BuiltAt = build.BuiltAt.ToString("O", CultureInfo.InvariantCulture), build.SettingsJson, build.StatsJson }, tx);

        await tx.CommitAsync(ct);
    }

    public async Task<BundleBuild?> GetBuildAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(string BuiltAt, string SettingsJson, string StatsJson)?>(new CommandDefinition(
            "SELECT built_at, settings_json, stats_json FROM bundle_builds WHERE job_id = @jobId",
            new { jobId }, cancellationToken: ct));
        if (row is not { } r) return null;
        return new BundleBuild
        {
            JobId = jobId,
            BuiltAt = ParseUtc(r.BuiltAt),
            SettingsJson = r.SettingsJson,
            StatsJson = r.StatsJson
        };
    }

    public async Task<IReadOnlyList<Bundle>> GetAllAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<BundleRow>(new CommandDefinition(
            "SELECT * FROM bundles WHERE job_id = @jobId ORDER BY id",
            new { jobId }, cancellationToken: ct));
        return rows.Select(r => r.ToBundle()).ToList();
    }

    public async Task<IReadOnlyDictionary<long, long>> GetMembershipAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long FileNodeId, long BundleId)>(new CommandDefinition(
            """
            SELECT bf.file_node_id, bf.bundle_id
            FROM bundle_files bf JOIN bundles b ON b.id = bf.bundle_id
            WHERE b.job_id = @jobId
            """,
            new { jobId }, cancellationToken: ct));
        return rows.ToDictionary(r => r.FileNodeId, r => r.BundleId);
    }

    public async Task<BundleComparisonCounts?> GetComparisonCountsAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var latest = await conn.QueryFirstOrDefaultAsync<(long Id, long AgainstJobId, string ComparedAt)?>(new CommandDefinition(
            """
            SELECT id, against_job_id, compared_at FROM comparisons
            WHERE source_job_id = @jobId AND against_job_id IS NOT NULL
            ORDER BY compared_at DESC, id DESC LIMIT 1
            """,
            new { jobId }, cancellationToken: ct));
        if (latest is not { } c) return null;

        var counts = await conn.QueryAsync<(long BundleId, string? State, long Files)>(new CommandDefinition(
            """
            SELECT bf.bundle_id, cf.state, COUNT(*) AS files
            FROM bundle_files bf
            JOIN comparison_files cf ON cf.file_node_id = bf.file_node_id AND cf.comparison_id = @id
            GROUP BY bf.bundle_id, cf.state
            """,
            new { id = c.Id }, cancellationToken: ct));

        return new BundleComparisonCounts(c.Id, c.AgainstJobId, ParseUtc(c.ComparedAt),
            counts.Where(x => x.State is not null).Select(x => (x.BundleId, x.State!, x.Files)).ToList());
    }

    private static async Task<long> InsertAsync(SqliteConnection conn, SqliteTransaction tx, Bundle b) =>
        await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO bundles
                (job_id, parent_bundle_id, kind, origin, part, root_path, root_directory_node_id,
                 suggested_name, styled_name, first_time, last_time, time_source, file_count, total_bytes,
                 directory_count, empty_directory_count, reason, facts_json, built_at)
            VALUES
                (@JobId, @ParentId, @Kind, @Origin, @Part, @RootPath, @RootDirectoryNodeId,
                 @SuggestedName, @StyledName, @FirstTime, @LastTime, @TimeSource, @FileCount, @TotalBytes,
                 @DirectoryCount, @EmptyDirectoryCount, @Reason, @FactsJson, @BuiltAt);
            SELECT last_insert_rowid();
            """,
            new
            {
                b.JobId,
                b.ParentId,
                Kind = b.Kind.ToCode(),
                Origin = b.Origin.ToCode(),
                Part = b.Part.ToCode(),
                b.RootPath,
                b.RootDirectoryNodeId,
                b.SuggestedName,
                b.StyledName,
                FirstTime = b.FirstTime?.ToString(LocalTimeFormat, CultureInfo.InvariantCulture),
                LastTime = b.LastTime?.ToString(LocalTimeFormat, CultureInfo.InvariantCulture),
                TimeSource = b.TimeSource.ToCode(),
                b.FileCount,
                b.TotalBytes,
                b.DirectoryCount,
                b.EmptyDirectoryCount,
                b.Reason,
                FactsJson = JsonSerializer.Serialize(b.Facts, Json),
                BuiltAt = b.BuiltAt.ToString("O", CultureInfo.InvariantCulture)
            }, tx);

    private static DateTime ParseUtc(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private sealed class BundleRow
    {
        public long Id { get; set; }
        public long JobId { get; set; }
        public long? ParentBundleId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string? Origin { get; set; }
        public string? Part { get; set; }
        public string? RootPath { get; set; }
        public long? RootDirectoryNodeId { get; set; }
        public string SuggestedName { get; set; } = string.Empty;
        public string? StyledName { get; set; }
        public string? FirstTime { get; set; }
        public string? LastTime { get; set; }
        public string? TimeSource { get; set; }
        public long FileCount { get; set; }
        public long TotalBytes { get; set; }
        public int DirectoryCount { get; set; }
        public int EmptyDirectoryCount { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string FactsJson { get; set; } = "{}";
        public string BuiltAt { get; set; } = string.Empty;

        public Bundle ToBundle() => new()
        {
            Id = Id,
            JobId = JobId,
            ParentId = ParentBundleId,
            Kind = VerdictCodes.ParseNullable<BundleKind>(Kind)!.Value,
            Origin = VerdictCodes.ParseNullable<DumpOrigin>(Origin),
            Part = VerdictCodes.ParseNullable<DumpPart>(Part),
            RootPath = RootPath,
            RootDirectoryNodeId = RootDirectoryNodeId,
            SuggestedName = SuggestedName,
            StyledName = StyledName,
            FirstTime = ParseLocal(FirstTime),
            LastTime = ParseLocal(LastTime),
            TimeSource = VerdictCodes.ParseNullable<TimeSource>(TimeSource),
            FileCount = FileCount,
            TotalBytes = TotalBytes,
            DirectoryCount = DirectoryCount,
            EmptyDirectoryCount = EmptyDirectoryCount,
            Reason = Reason,
            Facts = JsonSerializer.Deserialize<BundleFacts>(FactsJson, Json) ?? new BundleFacts(),
            BuiltAt = ParseUtc(BuiltAt)
        };

        private static DateTime? ParseLocal(string? text) =>
            text is null ? null : DateTime.ParseExact(text, LocalTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }
}

/// <summary>
/// Capture time, device and image origin from the facts Phase 2 stored (<c>file_facts</c>). A job that was only
/// scanned has none; the bundle builder then falls back to dates in names and modification times.
/// </summary>
public class FileFactCaptureInfoSource : ICaptureInfoSource
{
    private readonly DatabaseContext _db;

    public FileFactCaptureInfoSource(DatabaseContext db) => _db = db;

    public async Task<IReadOnlyDictionary<long, CaptureInfo>> GetAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<(long FileNodeId, string Fact, string Value)>(new CommandDefinition(
            """
            SELECT ff.file_node_id, ff.fact, ff.value
            FROM file_facts ff JOIN file_nodes f ON f.id = ff.file_node_id
            WHERE f.job_id = @jobId AND ff.fact IN (@takenAt, @device, @imageOrigin)
            """,
            new
            {
                jobId,
                takenAt = FileFactKey.TakenAt.ToCode(),
                device = FileFactKey.Device.ToCode(),
                imageOrigin = FileFactKey.ImageOrigin.ToCode()
            }, cancellationToken: ct));

        var result = new Dictionary<long, CaptureInfo>();
        foreach (var group in rows.GroupBy(r => r.FileNodeId))
        {
            DateTime? taken = null;
            string? device = null;
            ImageOrigin? origin = null;
            foreach (var (_, fact, value) in group)
            {
                if (fact == FileFactKey.TakenAt.ToCode()
                    && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    taken = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
                else if (fact == FileFactKey.Device.ToCode())
                    device = value;
                else if (fact == FileFactKey.ImageOrigin.ToCode() && VerdictCodes.TryParse<ImageOrigin>(value, out var o))
                    origin = o;
            }
            result[group.Key] = new CaptureInfo(taken, device, origin);
        }
        return result;
    }
}
