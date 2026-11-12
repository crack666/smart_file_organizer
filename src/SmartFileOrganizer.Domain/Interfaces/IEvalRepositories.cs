using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>Test cases from corrections (<c>eval_cases</c>). Local only.</summary>
public interface IEvalCaseRepository
{
    /// <summary>Inserts the case, or updates the one with the same kind, job and subject path. Sets <see cref="EvalCase.Id"/>.</summary>
    Task UpsertAsync(EvalCase evalCase, CancellationToken ct = default);

    Task<IReadOnlyList<EvalCase>> GetAllAsync(EvalCaseKind? kind = null, CancellationToken ct = default);
}

/// <summary>Stored evaluation runs (<c>eval_runs</c>).</summary>
public interface IEvalRunRepository
{
    /// <summary>Stores the run and sets <see cref="EvalRun.Id"/>.</summary>
    Task InsertAsync(EvalRun run, CancellationToken ct = default);

    Task<EvalRun?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>The newest runs first, optionally of one mode.</summary>
    Task<IReadOnlyList<EvalRun>> GetLatestAsync(EvalRunMode? mode, int count, CancellationToken ct = default);
}
