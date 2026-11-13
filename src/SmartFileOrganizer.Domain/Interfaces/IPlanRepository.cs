using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>Plans and their actions (<c>plans</c>, <c>plan_actions</c>).</summary>
public interface IPlanRepository
{
    /// <summary>Stores a new plan with its actions in one transaction; sets the ids.</summary>
    Task<Plan> CreateAsync(Plan plan, IReadOnlyList<PlanAction> actions, CancellationToken ct = default);

    Task<Plan?> GetAsync(long planId, CancellationToken ct = default);

    /// <summary>All plans of a job, newest first.</summary>
    Task<IReadOnlyList<Plan>> GetByJobAsync(long jobId, CancellationToken ct = default);

    /// <summary>The actions of a plan, ordered by id.</summary>
    Task<IReadOnlyList<PlanAction>> GetActionsAsync(long planId, CancellationToken ct = default);

    /// <summary>Writes kind, target, reason, note, origin, state and decision of the actions in one transaction.</summary>
    Task UpdateActionsAsync(IReadOnlyList<PlanAction> actions, CancellationToken ct = default);
}

/// <summary>
/// Places no target may ever lie in: the user profile and the system drive (on Windows), the home and system folders
/// (elsewhere). Abstracted so tests that work in a temporary folder of the profile can name their own.
/// </summary>
public interface IProtectedPlaces
{
    IReadOnlyList<string> Get();
}
