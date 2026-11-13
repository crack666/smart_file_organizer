using System.Globalization;
using System.Text.Json;
using Dapper;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>Stores the decisions of stage 2 in <c>file_decisions</c>; codes as text, the inputs as JSON lists.</summary>
public class FileDecisionRepository : IFileDecisionRepository
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly DatabaseContext _db;

    public FileDecisionRepository(DatabaseContext db) => _db = db;

    public async Task ReplaceAsync(long jobId, IReadOnlyList<FileDecision> decisions, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM file_decisions WHERE job_id = @jobId", new { jobId }, tx, cancellationToken: ct));
        if (decisions.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO file_decisions (file_node_id, job_id, worth, certainty, rule, basis,
                    target_id, target_path, target_rule, target_basis, target_unresolved, decided_at)
                VALUES (@FileNodeId, @JobId, @Worth, @Certainty, @Rule, @Basis,
                    @TargetId, @TargetPath, @TargetRule, @TargetBasis, @TargetUnresolved, @DecidedAt)
                """,
                decisions.Select(d => new
                {
                    d.FileNodeId,
                    JobId = jobId,
                    Worth = d.Worth?.ToCode(),
                    Certainty = d.Certainty.ToCode(),
                    d.Rule,
                    Basis = JsonSerializer.Serialize(d.Basis, Json),
                    TargetId = d.Target?.TargetId,
                    TargetPath = d.Target?.Path,
                    TargetRule = d.Target?.Rule,
                    TargetBasis = d.Target is { } t ? JsonSerializer.Serialize(t.Basis, Json) : null,
                    TargetUnresolved = d.Target is { Unresolved.Count: > 0 } u ? string.Join(",", u.Unresolved) : null,
                    DecidedAt = d.DecidedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                }).ToList(),
                tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyDictionary<long, FileDecision>> GetByJobAsync(long jobId, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            """
            SELECT d.file_node_id AS FileNodeId, d.job_id AS JobId, d.worth AS Worth, d.certainty AS Certainty,
                   d.rule AS Rule, d.basis AS Basis, d.target_id AS TargetId, d.target_path AS TargetPath,
                   d.target_rule AS TargetRule, d.target_basis AS TargetBasis, d.target_unresolved AS TargetUnresolved,
                   d.decided_at AS DecidedAt
            FROM file_decisions d WHERE d.job_id = @jobId
            """, new { jobId }, cancellationToken: ct));

        return rows.ToDictionary(r => r.FileNodeId, r => new FileDecision
        {
            JobId = r.JobId,
            FileNodeId = r.FileNodeId,
            Worth = VerdictCodes.ParseNullable<RetentionValue>(r.Worth),
            Certainty = VerdictCodes.ParseNullable<ResultCertainty>(r.Certainty) ?? ResultCertainty.Review,
            Rule = r.Rule,
            Basis = JsonSerializer.Deserialize<List<DecisionInput>>(r.Basis, Json) ?? [],
            Target = r.TargetId is null || r.TargetPath is null
                ? null
                : new TargetProposal(r.TargetId, r.TargetPath, r.TargetRule ?? string.Empty,
                    r.TargetBasis is null ? [] : JsonSerializer.Deserialize<List<DecisionInput>>(r.TargetBasis, Json) ?? [],
                    string.IsNullOrEmpty(r.TargetUnresolved) ? [] : r.TargetUnresolved.Split(',')),
            DecidedAt = DateTime.Parse(r.DecidedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        });
    }

    private sealed class Row
    {
        public long FileNodeId { get; set; }
        public long JobId { get; set; }
        public string? Worth { get; set; }
        public string Certainty { get; set; } = string.Empty;
        public string Rule { get; set; } = string.Empty;
        public string Basis { get; set; } = "[]";
        public string? TargetId { get; set; }
        public string? TargetPath { get; set; }
        public string? TargetRule { get; set; }
        public string? TargetBasis { get; set; }
        public string? TargetUnresolved { get; set; }
        public string DecidedAt { get; set; } = string.Empty;
    }
}
