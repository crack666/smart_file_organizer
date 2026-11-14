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

    /// <summary>
    /// Source files this plan's own checked copies made <c>backed_up</c> in a comparison (<c>library_entries</c>, not
    /// taken back). Their being in the library is the plan's doing, not a reason to refuse its next steps.
    /// </summary>
    Task<IReadOnlyList<string>> GetWrittenForwardSourcesAsync(long planId, CancellationToken ct = default);
}

/// <summary>
/// The journal of the execution (<c>plan_steps</c>), what the scan saw of a source, and the per-job switch that keeps
/// the sources untouched (https://github.com/crack666/smart_file_organizer/issues/12).
/// </summary>
public interface IPlanJournal
{
    /// <summary>Writes a step as started and sets its id; to be called before the file system is touched.</summary>
    Task<PlanStep> StartAsync(PlanStep step, CancellationToken ct = default);

    /// <summary>Finishes a step: state, checksum, detail and the time.</summary>
    Task FinishAsync(PlanStep step, CancellationToken ct = default);

    /// <summary>Every step of a plan, oldest first.</summary>
    Task<IReadOnlyList<PlanStep>> GetStepsAsync(long planId, CancellationToken ct = default);

    /// <summary>The scanned files of a job at <paramref name="path"/> (a file) or below it (a folder).</summary>
    Task<IReadOnlyList<ScannedFile>> GetScannedFilesAsync(long jobId, string path, CancellationToken ct = default);

    /// <summary>Whether the sources of the job are to be left untouched: then nothing is removed and a move only copies.</summary>
    Task<bool> GetKeepSourceAsync(long jobId, CancellationToken ct = default);

    Task SetKeepSourceAsync(long jobId, bool keep, CancellationToken ct = default);
}

/// <summary>
/// Places no target may ever lie in: the user profile and the system drive (on Windows), the home and system folders
/// (elsewhere). Abstracted so tests that work in a temporary folder of the profile can name their own.
/// </summary>
public interface IProtectedPlaces
{
    IReadOnlyList<string> Get();
}
