using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Plans;

/// <summary>
/// Named points of a run where a test can cut it off (by throwing), as a crash or Ctrl+C would. Nothing in the product
/// sets <see cref="At"/>.
/// </summary>
public sealed class PlanExecutionHooks
{
    /// <summary>The copy is written under its temporary name and not yet checked.</summary>
    public const string PartialWritten = "partial_written";

    /// <summary>The checked copy carries its name at the target; the journal step is not yet finished.</summary>
    public const string Copied = "copied";

    /// <summary>Every check before removing an original passed; the journal step is started, the original not yet moved.</summary>
    public const string BeforeHold = "before_hold";

    public Action<string, string>? At { get; set; }

    internal void Hit(string point, string path) => At?.Invoke(point, path);
}

/// <summary>Which actions a run is about, and whether it changes anything.</summary>
/// <param name="ActionIds">Only these actions; empty for every action of the plan. A sensitive action runs only when named.</param>
/// <param name="Execute">False: a dry run that reads, checks and reports, and writes nothing.</param>
/// <param name="Notices">Step 2 leaves a move notice in every folder whose originals went (default); false: none is written
/// in this run. Undo corrects existing notices either way.</param>
public sealed record PlanRunOptions(IReadOnlyList<long> ActionIds, bool Execute, bool Notices = true);

/// <summary>What a run did with the move notice of one source folder.</summary>
/// <param name="Outcome"><c>written</c> (created or changed), <c>removed</c> (no entry left), <c>would_write</c> or
/// <c>would_remove</c> (dry run),
/// <c>skipped</c> (a file that is not a notice carries the name; it is left alone) or <c>failed</c>.</param>
/// <param name="Entries">Entries in the notice afterwards, of every plan.</param>
public sealed record PlanNoticeItem(string Path, string Outcome, int Entries, string? Detail);

/// <summary>What a run did with one action.</summary>
/// <param name="Outcome"><c>done</c>, <c>would_run</c> (dry run), <c>skipped</c> (left as it was, see reasons),
/// <c>failed</c> (state <c>failed</c>, the original stays) or <c>partial</c> (an undo that could not take back everything).</param>
/// <param name="Reasons"><see cref="PlanRunReason"/> or <see cref="PlanProblem"/> codes, or a state code.</param>
public sealed record PlanRunItem(long ActionId, string Kind, string Outcome, string State, IReadOnlyList<string> Reasons,
    long Files, long Bytes, string? Detail);

/// <summary>Originals that wait for removal (<c>verified</c>): what <c>--remove-sources</c> would take away and the room it frees.</summary>
public sealed record PlanRemovalSummary(long Actions, long Files, long Bytes);

public sealed record PlanRunReport(
    long PlanId,
    string Mode,
    bool Executed,
    bool KeepSource,
    IReadOnlyList<PlanRunItem> Items,
    PlanRemovalSummary Removal,
    IReadOnlyList<string> EmptyFolders,
    IReadOnlyList<PlanNoticeItem>? Notices = null);

/// <summary>
/// Executes and takes back a plan (https://github.com/crack666/smart_file_organizer/issues/12). Only confirmed actions
/// run, and the checks of the plan run again before each one. In two steps, each confirmed on its own:
/// <list type="number">
/// <item><see cref="ApplyAsync"/>: every copy is written under a temporary name, read back whole and compared with the
/// original by its full checksum, then named; nothing is ever overwritten. <c>delete_source</c> reads the copy in the
/// library and the original whole. The action is then <c>verified</c>; a <c>copy</c> (or any action of a job set to keep
/// its sources) is <c>executed</c>.</item>
/// <item><see cref="RemoveSourcesAsync"/>: immediately before an original goes, both files are read again whole, the
/// original must be unchanged since the scan, and source and target must be two files. The original is then renamed
/// into the hold folder on its own drive, from where <see cref="UndoAsync"/> puts it back; only
/// <see cref="PurgeAsync"/> removes it for good.</item>
/// </list>
/// Every step is journalled before the file system is touched and finished afterwards; a run cut off in the middle is
/// recognised by the next run, which finishes or takes back the step it finds open.
/// </summary>
public sealed class PlanExecutor
{
    /// <summary>Folder in the root of the source job where removed originals are held.</summary>
    public const string HoldFolderName = ".sfo-hold";

    /// <summary>Suffix of a copy while it is written and checked; such a file never carries the target name.</summary>
    public const string PartialSuffix = ".sfo-partial";

    private readonly PlanService _plans;
    private readonly IPlanRepository _repo;
    private readonly IPlanJournal _journal;
    private readonly IScanJobRepository _jobs;
    private readonly IContentComparisonRepository _comparisons;
    private readonly IContentHasher _hasher;
    private readonly IFileSystemAccessor _fs;
    private readonly PlanExecutionHooks _hooks;
    private readonly TimeProvider _time;

    public PlanExecutor(
        PlanService plans,
        IPlanRepository repo,
        IPlanJournal journal,
        IScanJobRepository jobs,
        IContentComparisonRepository comparisons,
        IContentHasher hasher,
        IFileSystemAccessor fs,
        PlanExecutionHooks? hooks = null,
        TimeProvider? time = null)
    {
        _plans = plans;
        _repo = repo;
        _journal = journal;
        _jobs = jobs;
        _comparisons = comparisons;
        _hasher = hasher;
        _fs = fs;
        _hooks = hooks ?? new PlanExecutionHooks();
        _time = time ?? TimeProvider.System;
    }

    // ------------------------------------------------------------------ keep source

    /// <summary>Sets the person's switch "leave the source untouched" for a whole job.</summary>
    public async Task SetKeepSourceAsync(long jobId, bool keep, CancellationToken ct = default)
    {
        _ = await _jobs.GetByIdAsync(jobId, ct) ?? throw new JobReadException(ReadFailure.JobNotFound, $"Job {jobId} not found.");
        await _journal.SetKeepSourceAsync(jobId, keep, ct);
    }

    public async Task<bool> GetKeepSourceAsync(long jobId, CancellationToken ct = default)
    {
        _ = await _jobs.GetByIdAsync(jobId, ct) ?? throw new JobReadException(ReadFailure.JobNotFound, $"Job {jobId} not found.");
        return await _journal.GetKeepSourceAsync(jobId, ct);
    }

    // ------------------------------------------------------------------ step 1: copy and check

    /// <summary>
    /// Step 1 for every confirmed action: copy and check, or for <c>delete_source</c> check the copy in the library.
    /// No original is touched.
    /// </summary>
    public async Task<PlanRunReport> ApplyAsync(long planId, PlanRunOptions options, CancellationToken ct = default)
    {
        var run = await LoadAsync(planId, options, ct);
        var items = new List<PlanRunItem>();
        foreach (var a in run.Actions.OrderBy(a => a.Id))
        {
            if (!run.Selected(a)) continue;
            if (a.State != PlanActionState.Approved)
            {
                if (run.Named(a)) items.Add(Item(a, "skipped", [PlanRunReason.NotApproved.ToCode(), a.State.ToCode()], 0, 0));
                continue;
            }
            items.Add(await CopyActionAsync(run, a, ct));
        }
        return Report(run, "apply", items, []);
    }

    private async Task<PlanRunItem> CopyActionAsync(RunState run, PlanAction a, CancellationToken ct)
    {
        if (Guard(run, a, removes: a.Kind is PlanActionKind.DeleteSource or PlanActionKind.Quarantine) is { } refused)
            return refused;

        var (files, missing) = ListSource(run, a);
        if (missing is { } why)
            return Item(a, "skipped", [why.ToCode()], 0, 0);
        var bytes = files.Sum(f => f.Size);
        if (!await UnchangedSinceScanAsync(run, a, files, ct))
            return Item(a, "skipped", [PlanRunReason.SourceChanged.ToCode()], files.Count, bytes);

        var pairs = files.Select(f => (File: f, Target: TargetOf(a, f))).ToList();
        if (a.Kind == PlanActionKind.DeleteSource)
            return await VerifyDeleteSourceAsync(run, a, pairs, ct);

        var done = run.LiveCopies(a.Id).Select(s => s.TargetPath!).ToHashSet(PathComparer);
        var open = pairs.Where(p => !done.Contains(p.Target)).ToList();

        // Before anything is written: whatever lies at a target must have the content of its original.
        var present = new Dictionary<string, string>(PathComparer);
        var notes = new List<string>();
        foreach (var (file, target) in open)
        {
            if (Directory.Exists(target))
                return await FailAsync(run, a, PlanRunReason.TargetCollision, $"A folder lies at {target}.", files.Count, bytes, ct);
            if (!File.Exists(target) && !File.Exists(target + PartialSuffix)) continue;
            if (File.Exists(target + PartialSuffix))
                return await FailAsync(run, a, PlanRunReason.TargetCollision, $"A file lies at {target + PartialSuffix}.", files.Count, bytes, ct);
            if (!run.Execute)
            {
                notes.Add($"{target} exists; it is compared by checksum when executed");
                continue;
            }
            var stat = _hasher.Stat(target);
            var targetHash = stat.Exists && stat.Size == file.Size ? await HashAsync(target, ct) : null;
            var sourceHash = targetHash is null ? null : await HashAsync(file.Path, ct);
            if (targetHash is null || targetHash != sourceHash)
                return await FailAsync(run, a, PlanRunReason.TargetCollision, $"{target} exists with other content; nothing is overwritten.", files.Count, bytes, ct);
            if (a.Kind != PlanActionKind.Copy && SameFile(file.Path, target) is { } same)
                return await FailAsync(run, a, same, $"{file.Path} and {target} are one file.", files.Count, bytes, ct);
            present[target] = targetHash;
        }

        if (!run.Execute)
            return Item(a, "would_run", notes.Count > 0 ? [PlanRunReason.AlreadyPresent.ToCode()] : [], files.Count, bytes, Join(notes));

        foreach (var (file, target) in open)
        {
            if (present.TryGetValue(target, out var hash))
            {
                var step = await StartAsync(run, a, PlanStepKind.Present, file.Path, target, null, file.Size, hash, ct);
                await FinishAsync(run, step, PlanStepState.Done, "The same content lay at the target already.", ct);
                continue;
            }
            if (await CopyFileAsync(run, a, file, target, ct) is { } failure)
            {
                await RollBackCopiesAsync(run, a, ct);
                return await FailAsync(run, a, failure.Reason, failure.Detail, files.Count, bytes, ct);
            }
        }

        var keepsOriginal = a.Kind == PlanActionKind.Copy || run.KeepSource;
        a.State = keepsOriginal ? PlanActionState.Executed : PlanActionState.Verified;
        a.Note = keepsOriginal
            ? $"Copied and checked ({files.Count} files)" + (run.KeepSource && a.Kind != PlanActionKind.Copy ? "; the source stays (keep_source)." : ".")
            : $"Copied and checked ({files.Count} files); the original waits for sfo plan apply {run.Plan.Id} --remove-sources.";
        await SaveAsync(a, ct);
        var reasons = present.Count > 0 ? new List<string> { PlanRunReason.AlreadyPresent.ToCode() } : [];
        if (run.KeepSource && a.Kind != PlanActionKind.Copy) reasons.Add(PlanRunReason.KeepSource.ToCode());
        return Item(a, "done", reasons, files.Count, bytes);
    }

    /// <summary>
    /// <c>delete_source</c>, step 1: the library must hold every file of the source with the same full checksum, read
    /// now, and the stored checksum of the comparison must still fit; source and library file must be two files.
    /// </summary>
    private async Task<PlanRunItem> VerifyDeleteSourceAsync(RunState run, PlanAction a, IReadOnlyList<(SourceFile File, string Target)> pairs, CancellationToken ct)
    {
        var bytes = pairs.Sum(p => p.File.Size);
        foreach (var (file, target) in pairs)
        {
            if (!File.Exists(target))
                return await FailAsync(run, a, PlanRunReason.TargetMissing, $"{target} is not there.", pairs.Count, bytes, ct);
            if (SameFile(file.Path, target) is { } same)
                return await FailAsync(run, a, same, $"{file.Path} and {target} are not shown to be two files.", pairs.Count, bytes, ct);
        }
        if (!run.Execute)
            return Item(a, "would_run", [], pairs.Count, bytes);

        foreach (var (file, target) in pairs)
        {
            if (await CompareAsync(run, a, file.Path, target, expected: null, ct) is { } failure)
                return await FailAsync(run, a, failure.Reason, failure.Detail, pairs.Count, bytes, ct);
            var hash = await HashAsync(file.Path, ct);
            var step = await StartAsync(run, a, PlanStepKind.Verify, file.Path, target, null, file.Size, hash, ct);
            await FinishAsync(run, step, PlanStepState.Done, "The library holds the same content.", ct);
        }

        a.State = run.KeepSource ? PlanActionState.Executed : PlanActionState.Verified;
        a.Note = $"The library holds the same content ({pairs.Count} files); the original waits for sfo plan apply {run.Plan.Id} --remove-sources.";
        await SaveAsync(a, ct);
        return Item(a, "done", [], pairs.Count, bytes);
    }

    /// <summary>Copies one file: temporary name, full checksum of the copy read back, then the target name. Null when it worked.</summary>
    private async Task<(PlanRunReason Reason, string Detail)?> CopyFileAsync(RunState run, PlanAction a, SourceFile file, string target, CancellationToken ct)
    {
        var before = _hasher.Stat(file.Path);
        if (!before.Exists)
            return (PlanRunReason.SourceMissing, $"{file.Path} is gone.");
        var sourceHash = await HashAsync(file.Path, ct);
        if (sourceHash is null)
            return (PlanRunReason.IoError, $"{file.Path} could not be read.");

        try
        {
            await MakeFoldersAsync(run, a, Path.GetDirectoryName(target)!, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (PlanRunReason.IoError, ex.Message);
        }

        var partial = target + PartialSuffix;
        var step = await StartAsync(run, a, PlanStepKind.Copy, file.Path, target, null, file.Size, sourceHash, ct);
        try
        {
            await CopyContentAsync(file.Path, partial, ct);
            _hooks.Hit(PlanExecutionHooks.PartialWritten, partial);

            var copyHash = await HashAsync(partial, ct);
            var after = _hasher.Stat(file.Path);
            if (!after.Exists || after.Size != before.Size || after.LastWriteTicks != before.LastWriteTicks)
                return await FailStepAsync(run, step, partial, PlanRunReason.SourceChanged, $"{file.Path} changed while it was copied.", ct);
            if (copyHash != sourceHash)
                return await FailStepAsync(run, step, partial, PlanRunReason.HashMismatch,
                    $"The copy of {file.Path} read back has another checksum; it was removed.", ct);

            try
            {
                File.Move(partial, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                return await FailStepAsync(run, step, partial, PlanRunReason.TargetCollision, $"{target} appeared during the copy; nothing is overwritten.", ct);
            }
            _hooks.Hit(PlanExecutionHooks.Copied, target);
            await FinishAsync(run, step, PlanStepState.Done, null, ct);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await FailStepAsync(run, step, partial, PlanRunReason.IoError, ex.Message, ct);
        }
    }

    private async Task<(PlanRunReason Reason, string Detail)?> FailStepAsync(
        RunState run, PlanStep step, string partial, PlanRunReason reason, string detail, CancellationToken ct)
    {
        TryDeleteFile(partial);
        await FinishAsync(run, step, PlanStepState.Failed, $"{reason.ToCode()}: {detail}", ct);
        return (reason, detail);
    }

    private static async Task CopyContentAsync(string source, string partial, CancellationToken ct)
    {
        await using (var input = new FileStream(source, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.SequentialScan | FileOptions.Asynchronous
        }))
        await using (var output = new FileStream(partial, new FileStreamOptions
        {
            // CreateNew: a file that appears under the temporary name is never overwritten either.
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.WriteThrough | FileOptions.Asynchronous, PreallocationSize = 0
        }))
        {
            await input.CopyToAsync(output, ct);
            await output.FlushAsync(ct);
        }
        File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(source));
    }

    /// <summary>Creates the missing folders down to <paramref name="folder"/>, one journal step each, so the undo removes them again.</summary>
    private async Task MakeFoldersAsync(RunState run, PlanAction a, string folder, CancellationToken ct)
    {
        var missing = new Stack<string>();
        for (var dir = folder; !string.IsNullOrEmpty(dir) && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir))
            missing.Push(dir);
        while (missing.TryPop(out var dir))
        {
            var step = await StartAsync(run, a, PlanStepKind.MakeFolder, dir, null, null, 0, null, ct);
            Directory.CreateDirectory(dir);
            await FinishAsync(run, step, PlanStepState.Done, null, ct);
        }
    }

    // ------------------------------------------------------------------ step 2: remove originals

    /// <summary>
    /// Step 2, confirmed on its own: every <c>verified</c> action gives up its originals. Immediately before each one the
    /// original and the file at the target are read whole again and must both have the checksum of step 1, the
    /// original must be unchanged since the scan, and both must be two files. The original is renamed into the hold
    /// folder on its own drive; nothing is removed for good here.
    /// </summary>
    public async Task<PlanRunReport> RemoveSourcesAsync(long planId, PlanRunOptions options, CancellationToken ct = default)
    {
        var run = await LoadAsync(planId, options, ct);
        var items = new List<PlanRunItem>();
        var emptied = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var a in run.Actions.OrderBy(a => a.Id))
        {
            if (!run.Selected(a)) continue;
            if (a.State != PlanActionState.Verified)
            {
                if (run.Named(a)) items.Add(Item(a, "skipped", [a.State.ToCode()], 0, 0));
                continue;
            }
            items.Add(await RemoveActionAsync(run, a, emptied, ct));
        }
        var notices = await UpdateNoticesAsync(run, create: options.Notices, ct);
        return Report(run, "remove_sources", items, emptied.ToList(), notices);
    }

    private async Task<PlanRunItem> RemoveActionAsync(RunState run, PlanAction a, ISet<string> emptied, CancellationToken ct)
    {
        if (Guard(run, a, removes: true) is { } refused)
            return refused;

        var pending = run.Checked(a.Id).Where(s => run.ActiveHold(a.Id, s.SourcePath) is null).ToList();
        var bytes = pending.Sum(s => s.Bytes);
        if (!run.Execute)
        {
            // The dry run reports the move notices as they would be after this action.
            var now = DateTime.UtcNow;
            run.WouldHold.AddRange(pending.Select(s => new PlanStep
            {
                PlanId = run.Plan.Id, ActionId = a.Id, Kind = PlanStepKind.Hold, State = PlanStepState.Done,
                SourcePath = s.SourcePath, TargetPath = s.TargetPath, Bytes = s.Bytes, StartedAt = now, FinishedAt = now
            }));
            return Item(a, "would_run", [], pending.Count, bytes);
        }

        var scanned = (await _journal.GetScannedFilesAsync(run.Job.Id, a.SourcePath, ct)).ToDictionary(f => f.FullPath, PathComparer);
        foreach (var s in pending)
        {
            var source = s.SourcePath;
            var target = s.TargetPath!;
            var stat = _hasher.Stat(source);
            if (!stat.Exists)
                return await FailAsync(run, a, PlanRunReason.SourceMissing, $"{source} is gone.", pending.Count, bytes, ct);
            if (!scanned.TryGetValue(source, out var seen) || seen.Size != stat.Size || seen.LastWriteTime.Ticks != stat.LastWriteTicks)
                return await FailAsync(run, a, PlanRunReason.SourceChanged, $"{source} is not as the scan saw it.", pending.Count, bytes, ct);
            if (!File.Exists(target))
                return await FailAsync(run, a, PlanRunReason.TargetMissing, $"{target} is not there.", pending.Count, bytes, ct);
            if (SameFile(source, target) is { } same)
                return await FailAsync(run, a, same, $"{source} and {target} are not shown to be two files.", pending.Count, bytes, ct);
            if (await CompareAsync(run, a, source, target, s.Hash, ct) is { } failure)
                return await FailAsync(run, a, failure.Reason, failure.Detail, pending.Count, bytes, ct);

            var hold = HoldPath(run, a, source);
            if (!SameVolume(source, hold))
                return await FailAsync(run, a, PlanRunReason.OtherVolume, $"{hold} is not on the drive of {source}.", pending.Count, bytes, ct);
            if (File.Exists(hold) || Directory.Exists(hold))
                return await FailAsync(run, a, PlanRunReason.IoError, $"{hold} is taken.", pending.Count, bytes, ct);

            var step = await StartAsync(run, a, PlanStepKind.Hold, source, target, hold, s.Bytes, s.Hash, ct);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(hold)!);
                _hooks.Hit(PlanExecutionHooks.BeforeHold, source);
                File.Move(source, hold, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await FinishAsync(run, step, PlanStepState.Failed, $"{PlanRunReason.IoError.ToCode()}: {ex.Message}", ct);
                return await FailAsync(run, a, PlanRunReason.IoError, ex.Message, pending.Count, bytes, ct);
            }
            await FinishAsync(run, step, PlanStepState.Done, null, ct);
            NoteEmptyFolders(run, Path.GetDirectoryName(source)!, emptied);
        }

        a.State = PlanActionState.Executed;
        a.Note = $"Done: the originals ({pending.Count} files) lie in the hold folder until sfo plan purge {run.Plan.Id}; sfo plan undo puts them back.";
        await SaveAsync(a, ct);
        return Item(a, "done", [], pending.Count, bytes);
    }

    // ------------------------------------------------------------------ undo

    /// <summary>
    /// Takes back what the execution did, newest action first: originals come back from the hold folder (or, when they
    /// were purged, are copied back from the checked file at the target), then the copies this tool wrote are removed
    /// once the original is back with its checksum, then the folders it created when they are empty. A copy that
    /// changed since is kept, and so is the only remaining copy of anything.
    /// </summary>
    public async Task<PlanRunReport> UndoAsync(long planId, PlanRunOptions options, CancellationToken ct = default)
    {
        var run = await LoadAsync(planId, options, ct);
        var items = new List<PlanRunItem>();
        foreach (var a in run.Actions.OrderByDescending(a => a.Id))
        {
            if (!run.Selected(a) || a.State is PlanActionState.Undone) continue;
            if (!run.Steps.Any(s => s.ActionId == a.Id && s.State == PlanStepState.Done)) continue;
            items.Add(await UndoActionAsync(run, a, ct));
        }
        var notices = await UpdateNoticesAsync(run, create: false, ct);
        if (run.Execute)
            CleanHoldFolder(run);
        return Report(run, "undo", items, [], notices);
    }

    private async Task<PlanRunItem> UndoActionAsync(RunState run, PlanAction a, CancellationToken ct)
    {
        var steps = run.Steps.Where(s => s.ActionId == a.Id && s.State == PlanStepState.Done).OrderByDescending(s => s.Id).ToList();
        var holds = steps.Where(s => s.Kind == PlanStepKind.Hold && run.ActiveHold(a.Id, s.SourcePath)?.Id == s.Id).ToList();
        var copies = run.LiveCopies(a.Id).Where(s => s.Kind == PlanStepKind.Copy).OrderByDescending(s => s.Id).ToList();
        var files = holds.Select(h => h.SourcePath).Concat(copies.Select(c => c.SourcePath)).Distinct(PathComparer).Count();
        var bytes = holds.Sum(h => h.Bytes) + copies.Sum(c => c.Bytes);
        if (!run.Execute)
        {
            run.WouldReturn.Add(a.Id);
            var purged = holds.Count(h => run.Purged(h));
            return Item(a, "would_run", [], files, bytes,
                purged > 0 ? $"{purged} originals were purged; they are copied back from the target" : null);
        }

        var reasons = new List<string>();
        var details = new List<string>();
        void Refuse(PlanRunReason reason, string detail)
        {
            if (!reasons.Contains(reason.ToCode())) reasons.Add(reason.ToCode());
            details.Add(detail);
        }

        // 1. Originals back.
        foreach (var h in holds)
        {
            var source = h.SourcePath;
            if (File.Exists(source) || Directory.Exists(source))
            {
                Refuse(PlanRunReason.OriginalOccupied, $"{source} is taken; the original stays at {h.HoldPath}.");
                continue;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                if (!run.Purged(h))
                {
                    if (await HashAsync(h.HoldPath!, ct) != h.Hash)
                    {
                        Refuse(File.Exists(h.HoldPath) ? PlanRunReason.HeldChanged : PlanRunReason.NotUndoable, $"{h.HoldPath} is not the held original.");
                        continue;
                    }
                    var step = await StartAsync(run, a, PlanStepKind.Restore, source, h.TargetPath, h.HoldPath, h.Bytes, h.Hash, ct);
                    File.Move(h.HoldPath!, source, overwrite: false);
                    await FinishAsync(run, step, PlanStepState.Done, "Back from the hold folder.", ct);
                }
                else
                {
                    // Purged: the checked file at the target is the original's content; copy it back.
                    if (h.TargetPath is not { } target || await HashAsync(target, ct) != h.Hash)
                    {
                        Refuse(PlanRunReason.NotUndoable, $"{source} was purged and {h.TargetPath} no longer has its content.");
                        continue;
                    }
                    var step = await StartAsync(run, a, PlanStepKind.Restore, source, target, null, h.Bytes, h.Hash, ct);
                    var partial = source + PartialSuffix;
                    await CopyContentAsync(target, partial, ct);
                    if (await HashAsync(partial, ct) != h.Hash)
                    {
                        TryDeleteFile(partial);
                        await FinishAsync(run, step, PlanStepState.Failed, $"{PlanRunReason.HashMismatch.ToCode()}: the copy back did not match.", ct);
                        Refuse(PlanRunReason.HashMismatch, $"Copying {target} back to {source} did not match; nothing changed.");
                        continue;
                    }
                    File.Move(partial, source, overwrite: false);
                    await FinishAsync(run, step, PlanStepState.Done, "Copied back from the target; the original had been purged.", ct);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Refuse(PlanRunReason.IoError, ex.Message);
            }
        }

        // 2. Copies this tool wrote, once their original is back with its checksum.
        foreach (var c in copies)
        {
            var target = c.TargetPath!;
            if (!File.Exists(target)) continue;
            if (await HashAsync(c.SourcePath, ct) != c.Hash)
            {
                Refuse(PlanRunReason.NotUndoable, $"{c.SourcePath} is not back with its content, so {target} stays.");
                continue;
            }
            if (await HashAsync(target, ct) != c.Hash)
            {
                Refuse(PlanRunReason.TargetChanged, $"{target} changed since it was copied; it stays.");
                continue;
            }
            if (SameFile(c.SourcePath, target) is { } same)
            {
                Refuse(same, $"{c.SourcePath} and {target} are not shown to be two files; {target} stays.");
                continue;
            }
            var step = await StartAsync(run, a, PlanStepKind.Uncopy, c.SourcePath, target, null, c.Bytes, c.Hash, ct);
            try
            {
                File.Delete(target);
                await FinishAsync(run, step, PlanStepState.Done, null, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await FinishAsync(run, step, PlanStepState.Failed, ex.Message, ct);
                Refuse(PlanRunReason.IoError, ex.Message);
            }
        }

        // 3. Folders it created, when empty.
        await RemoveMadeFoldersAsync(run, a, ct);

        if (reasons.Count == 0)
        {
            a.State = PlanActionState.Undone;
            a.Note = "Taken back.";
            await SaveAsync(a, ct);
            return Item(a, "done", [], files, bytes);
        }
        a.Note = "Partly taken back: " + Join(details);
        await SaveAsync(a, ct);
        return Item(a, "partial", reasons, files, bytes, Join(details));
    }

    private async Task RemoveMadeFoldersAsync(RunState run, PlanAction a, CancellationToken ct)
    {
        var made = run.Steps.Where(s => s.ActionId == a.Id && s.Kind == PlanStepKind.MakeFolder && s.State == PlanStepState.Done)
            .Where(s => !run.Steps.Any(r => r.Id > s.Id && r.Kind == PlanStepKind.RemoveFolder && r.State == PlanStepState.Done
                                            && PathComparer.Equals(r.SourcePath, s.SourcePath)))
            .OrderByDescending(s => s.Id)
            .ToList();
        foreach (var m in made)
        {
            if (!Directory.Exists(m.SourcePath) || Directory.EnumerateFileSystemEntries(m.SourcePath).Any()) continue;
            var step = await StartAsync(run, a, PlanStepKind.RemoveFolder, m.SourcePath, null, null, 0, null, ct);
            try
            {
                Directory.Delete(m.SourcePath, recursive: false);
                await FinishAsync(run, step, PlanStepState.Done, null, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await FinishAsync(run, step, PlanStepState.Failed, ex.Message, ct);
            }
        }
    }

    /// <summary>Takes back the copies of an action that failed in step 1, so a failed action leaves nothing at the target.</summary>
    private async Task RollBackCopiesAsync(RunState run, PlanAction a, CancellationToken ct)
    {
        foreach (var c in run.LiveCopies(a.Id).Where(s => s.Kind == PlanStepKind.Copy).OrderByDescending(s => s.Id).ToList())
        {
            var target = c.TargetPath!;
            if (!File.Exists(target) || await HashAsync(c.SourcePath, ct) != c.Hash || await HashAsync(target, ct) != c.Hash
                || SameFile(c.SourcePath, target) is not null)
                continue;
            var step = await StartAsync(run, a, PlanStepKind.Uncopy, c.SourcePath, target, null, c.Bytes, c.Hash, ct);
            try
            {
                File.Delete(target);
                await FinishAsync(run, step, PlanStepState.Done, "The action failed; its copy was taken back.", ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await FinishAsync(run, step, PlanStepState.Failed, ex.Message, ct);
            }
        }
        await RemoveMadeFoldersAsync(run, a, ct);
    }

    // ------------------------------------------------------------------ purge

    /// <summary>
    /// Removes held originals for good; this cannot be taken back from the hold folder. Only an original whose file at
    /// the target still has its checksum and is another file is purged: the last copy of anything is never removed.
    /// </summary>
    public async Task<PlanRunReport> PurgeAsync(long planId, PlanRunOptions options, CancellationToken ct = default)
    {
        var run = await LoadAsync(planId, options, ct);
        var items = new List<PlanRunItem>();
        foreach (var a in run.Actions.OrderBy(a => a.Id))
        {
            if (!run.Selected(a)) continue;
            var held = run.Steps.Where(s => s.ActionId == a.Id && s.Kind == PlanStepKind.Hold && s.State == PlanStepState.Done
                                           && run.ActiveHold(a.Id, s.SourcePath)?.Id == s.Id && !run.Purged(s)).ToList();
            if (held.Count == 0) continue;
            var bytes = held.Sum(h => h.Bytes);
            if (!run.Execute)
            {
                items.Add(Item(a, "would_run", [], held.Count, bytes));
                continue;
            }

            var reasons = new List<string>();
            var details = new List<string>();
            var purged = 0;
            var purgedBytes = 0L;
            foreach (var h in held)
            {
                var target = h.TargetPath!;
                PlanRunReason? refuse = !File.Exists(target) ? PlanRunReason.TargetMissing
                    : await HashAsync(target, ct) != h.Hash ? PlanRunReason.TargetChanged
                    : SameFile(h.HoldPath!, target);
                if (refuse is { } r)
                {
                    if (!reasons.Contains(r.ToCode())) reasons.Add(r.ToCode());
                    details.Add($"{h.HoldPath} stays: {target} is not a checked copy of it.");
                    continue;
                }
                var step = await StartAsync(run, a, PlanStepKind.Purge, h.SourcePath, target, h.HoldPath, h.Bytes, h.Hash, ct);
                try
                {
                    File.Delete(h.HoldPath!);
                    await FinishAsync(run, step, PlanStepState.Done, null, ct);
                    purged++;
                    purgedBytes += h.Bytes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await FinishAsync(run, step, PlanStepState.Failed, ex.Message, ct);
                    if (!reasons.Contains(PlanRunReason.IoError.ToCode())) reasons.Add(PlanRunReason.IoError.ToCode());
                    details.Add(ex.Message);
                }
            }
            items.Add(Item(a, reasons.Count == 0 ? "done" : "partial", reasons, purged, purgedBytes, Join(details)));
        }
        if (run.Execute)
            CleanHoldFolder(run);
        return Report(run, "purge", items, []);
    }

    // ------------------------------------------------------------------ run state, recovery

    private sealed record SourceFile(string Path, string Relative, long Size, long LastWriteTicks);

    private sealed class RunState
    {
        public required Plan Plan { get; init; }
        public required ScanJob Job { get; init; }
        public required IReadOnlyList<PlanAction> Actions { get; init; }
        public required IReadOnlyDictionary<long, IReadOnlyList<PlanProblem>> Problems { get; init; }
        public required List<PlanStep> Steps { get; init; }
        public required IReadOnlyDictionary<long, FileComparison> Matches { get; init; }
        public required PlanRunOptions Options { get; init; }
        public bool KeepSource { get; init; }
        public bool Execute => Options.Execute;

        /// <summary>Dry run only: the originals step 2 would take away (unsaved hold steps, id 0).</summary>
        public List<PlanStep> WouldHold { get; } = [];

        /// <summary>Dry run only: actions whose originals the undo would put back.</summary>
        public HashSet<long> WouldReturn { get; } = [];

        public bool Named(PlanAction a) => Options.ActionIds.Contains(a.Id);
        public bool Selected(PlanAction a) => a.Kind != PlanActionKind.Leave && (Options.ActionIds.Count == 0 || Named(a));

        /// <summary>Copies and present files of an action that were not taken back.</summary>
        public IEnumerable<PlanStep> LiveCopies(long actionId) => Steps
            .Where(s => s.ActionId == actionId && s.State == PlanStepState.Done && s.Kind is PlanStepKind.Copy or PlanStepKind.Present)
            .Where(s => !Steps.Any(u => u.Id > s.Id && u.ActionId == actionId && u.Kind == PlanStepKind.Uncopy && u.State == PlanStepState.Done
                                        && PathComparer.Equals(u.TargetPath, s.TargetPath)));

        /// <summary>The checked pairs of step 1: copies, present files and verified library files.</summary>
        public IEnumerable<PlanStep> Checked(long actionId) => LiveCopies(actionId)
            .Concat(Steps.Where(s => s.ActionId == actionId && s.Kind == PlanStepKind.Verify && s.State == PlanStepState.Done))
            .OrderBy(s => s.Id);

        /// <summary>The hold step of an original that was not put back since, or null.</summary>
        public PlanStep? ActiveHold(long actionId, string source)
        {
            var last = Steps.LastOrDefault(s => s.ActionId == actionId && s.State == PlanStepState.Done
                                                && s.Kind is PlanStepKind.Hold or PlanStepKind.Restore && PathComparer.Equals(s.SourcePath, source));
            return last?.Kind == PlanStepKind.Hold ? last : null;
        }

        public bool Purged(PlanStep hold) => Steps.Any(s => s.Id > hold.Id && s.Kind == PlanStepKind.Purge && s.State == PlanStepState.Done
                                                            && PathComparer.Equals(s.HoldPath, hold.HoldPath));
    }

    private async Task<RunState> LoadAsync(long planId, PlanRunOptions options, CancellationToken ct)
    {
        var (plan, job, libraries, actions, ctx) = await _plans.LoadForRunAsync(planId, ct);
        foreach (var id in options.ActionIds)
        {
            if (actions.All(a => a.Id != id))
                throw new PlanException(PlanFailure.ActionNotFound, $"Plan {planId} has no action {id}.");
        }
        // Whatever lies at a target is judged by content during the run, not refused by name.
        var problems = PlanChecks.CheckAll(actions, ctx with { Exists = _ => false });
        var set = libraries.Count > 0
            ? (await LibraryComparisonSet.LoadAsync(_comparisons, job.Id, libraries.Select(l => l.Id).ToList(), ct)).Set
            : null;
        var run = new RunState
        {
            Plan = plan,
            Job = job,
            Actions = actions,
            Problems = problems,
            Steps = (await _journal.GetStepsAsync(planId, ct)).ToList(),
            Matches = set?.Rows ?? new Dictionary<long, FileComparison>(),
            Options = options,
            KeepSource = await _journal.GetKeepSourceAsync(job.Id, ct)
        };
        if (options.Execute)
            await RecoverAsync(run, ct);
        return run;
    }

    /// <summary>
    /// Settles every step a cut-off run left <c>started</c>, by what the file system shows: a copy under its temporary
    /// name is removed; a copy that carries its name and the checksum of its original counts as done; an original that
    /// is in the hold folder and no longer in its place counts as held. Nothing is lost either way, since an original
    /// is only ever renamed and a copy only ever named after its check.
    /// </summary>
    private async Task RecoverAsync(RunState run, CancellationToken ct)
    {
        foreach (var s in run.Steps.Where(s => s.State == PlanStepState.Started).ToList())
        {
            var (state, detail) = s.Kind switch
            {
                PlanStepKind.Copy => await RecoverCopyAsync(s, ct),
                PlanStepKind.Hold => !File.Exists(s.SourcePath) && File.Exists(s.HoldPath)
                    ? (PlanStepState.Done, "Finished after an interruption: the original lies in the hold folder.")
                    : (PlanStepState.Failed, "Interrupted before the original was moved; it is in its place."),
                PlanStepKind.Restore => await RecoverRestoreAsync(s, ct),
                PlanStepKind.Uncopy => !File.Exists(s.TargetPath)
                    ? (PlanStepState.Done, "Finished after an interruption.")
                    : (PlanStepState.Failed, "Interrupted; the copy is still there."),
                PlanStepKind.Purge => !File.Exists(s.HoldPath)
                    ? (PlanStepState.Done, "Finished after an interruption.")
                    : (PlanStepState.Failed, "Interrupted; the held original is still there."),
                PlanStepKind.MakeFolder => Directory.Exists(s.SourcePath)
                    ? (PlanStepState.Done, "Finished after an interruption.")
                    : (PlanStepState.Failed, "Interrupted before the folder was made."),
                PlanStepKind.RemoveFolder => !Directory.Exists(s.SourcePath)
                    ? (PlanStepState.Done, "Finished after an interruption.")
                    : (PlanStepState.Failed, "Interrupted; the folder is still there."),
                // The notice is written whole under a temporary name; the next run brings it in line with the journal.
                PlanStepKind.Notice => RecoverNotice(s),
                _ => (PlanStepState.Failed, "Interrupted before the step was written.")
            };
            await FinishAsync(run, s, state, detail, ct);
        }
    }

    private static (PlanStepState, string) RecoverNotice(PlanStep s)
    {
        TryDeleteFile(s.SourcePath + PartialSuffix);
        return (PlanStepState.Failed, "Interrupted while the move notice was written; it is written again by the next run.");
    }

    private async Task<(PlanStepState, string)> RecoverCopyAsync(PlanStep s, CancellationToken ct)
    {
        var partial = s.TargetPath + PartialSuffix;
        if (File.Exists(partial))
        {
            TryDeleteFile(partial);
            return (PlanStepState.Failed, "Interrupted during the copy; the unfinished copy was removed.");
        }
        if (File.Exists(s.TargetPath) && await HashAsync(s.TargetPath!, ct) == s.Hash)
            return (PlanStepState.Done, "Finished after an interruption: the copy at the target has the checksum of the original.");
        return (PlanStepState.Failed, "Interrupted before a checked copy carried the target name.");
    }

    private async Task<(PlanStepState, string)> RecoverRestoreAsync(PlanStep s, CancellationToken ct)
    {
        if (s.HoldPath is { } hold)
            return !File.Exists(hold) && File.Exists(s.SourcePath)
                ? (PlanStepState.Done, "Finished after an interruption.")
                : (PlanStepState.Failed, "Interrupted; the original is still in the hold folder.");
        var partial = s.SourcePath + PartialSuffix;
        if (File.Exists(partial))
        {
            TryDeleteFile(partial);
            return (PlanStepState.Failed, "Interrupted during the copy back; the unfinished copy was removed.");
        }
        return File.Exists(s.SourcePath) && await HashAsync(s.SourcePath, ct) == s.Hash
            ? (PlanStepState.Done, "Finished after an interruption.")
            : (PlanStepState.Failed, "Interrupted before the original was back.");
    }

    // ------------------------------------------------------------------ checks

    /// <summary>The checks before an action: confirmation by name for sensitive actions, the plan's checks, the job's switch.</summary>
    private PlanRunItem? Guard(RunState run, PlanAction a, bool removes)
    {
        if ((a.SingleDecision || a.Sensitive) && !run.Named(a))
            return Item(a, "skipped", [PlanRunReason.SingleDecision.ToCode()], a.Files, a.Bytes);
        if (run.Problems[a.Id] is { Count: > 0 } problems)
            return Item(a, "skipped", problems.Select(p => p.ToCode()).ToList(), a.Files, a.Bytes);
        if (removes && run.KeepSource)
            return Item(a, "skipped", [PlanRunReason.KeepSource.ToCode()], a.Files, a.Bytes);
        return null;
    }

    /// <summary>The files of the source, without following a link; the reason when the source is gone or is or holds a link.</summary>
    private static (IReadOnlyList<SourceFile> Files, PlanRunReason? Why) ListSource(RunState run, PlanAction a)
    {
        // No link on the way from the job root down to the source either.
        for (var dir = Path.GetDirectoryName(a.SourcePath);
             dir is not null && PlanChecks.IsAtOrBelow(dir, run.Job.RootPath) && !PathComparer.Equals(PlanChecks.Normalize(dir), PlanChecks.Normalize(run.Job.RootPath));
             dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return ([], PlanRunReason.SourceHasLink);
        }

        try
        {
            if (a.SourceKind == PlanSourceKind.File)
            {
                var info = new FileInfo(a.SourcePath);
                if (!info.Exists) return ([], PlanRunReason.SourceMissing);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return ([], PlanRunReason.SourceHasLink);
                return ([new SourceFile(info.FullName, info.Name, info.Length, info.LastWriteTimeUtc.Ticks)], null);
            }

            var root = new DirectoryInfo(a.SourcePath);
            if (!root.Exists) return ([], PlanRunReason.SourceMissing);
            if (root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return ([], PlanRunReason.SourceHasLink);
            var files = new List<SourceFile>();
            var stack = new Stack<DirectoryInfo>([root]);
            while (stack.TryPop(out var dir))
            {
                foreach (var entry in dir.EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) return ([], PlanRunReason.SourceHasLink);
                    if (entry is DirectoryInfo sub) stack.Push(sub);
                    // A move notice is the tool's own note, not content; the scan leaves it out as well.
                    else if (entry is FileInfo f && !IsNotice(f.FullName))
                        files.Add(new SourceFile(f.FullName, Path.GetRelativePath(root.FullName, f.FullName), f.Length, f.LastWriteTimeUtc.Ticks));
                }
            }
            return (files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], PlanRunReason.SourceMissing);
        }
    }

    /// <summary>The same files with the same size and modification time as the scan saw them.</summary>
    private async Task<bool> UnchangedSinceScanAsync(RunState run, PlanAction a, IReadOnlyList<SourceFile> files, CancellationToken ct)
    {
        var scanned = await _journal.GetScannedFilesAsync(run.Job.Id, a.SourcePath, ct);
        if (scanned.Count != files.Count) return false;
        var byPath = scanned.ToDictionary(f => f.FullPath, PathComparer);
        return files.All(f => byPath.TryGetValue(f.Path, out var s) && s.Size == f.Size && s.LastWriteTime.Ticks == f.LastWriteTicks);
    }

    /// <summary>
    /// Reads both files whole and compares their checksums with each other and with <paramref name="expected"/> (step 1),
    /// and for a file the library comparison matched, with the checksum stored there.
    /// </summary>
    private async Task<(PlanRunReason Reason, string Detail)?> CompareAsync(
        RunState run, PlanAction a, string source, string target, string? expected, CancellationToken ct)
    {
        var sourceHash = await HashAsync(source, ct);
        if (sourceHash is null)
            return (PlanRunReason.SourceMissing, $"{source} could not be read.");
        if (expected is not null && sourceHash != expected)
            return (PlanRunReason.SourceChanged, $"{source} no longer has the checksum it was checked with.");
        if (a.SourceKind == PlanSourceKind.File && a.FileNodeId is { } id && run.Matches.TryGetValue(id, out var match)
            && match.MatchHash is { } stored && a.Kind == PlanActionKind.DeleteSource && stored != sourceHash)
            return (PlanRunReason.SourceChanged, $"{source} no longer has the checksum the comparison found in the library.");
        var targetHash = await HashAsync(target, ct);
        if (targetHash is null)
            return (PlanRunReason.TargetMissing, $"{target} could not be read.");
        if (targetHash != sourceHash)
            return (expected is null ? PlanRunReason.HashMismatch : PlanRunReason.TargetChanged,
                $"{target} does not have the content of {source}.");
        return null;
    }

    /// <summary>Null when the file system names the two paths as two files; else why not.</summary>
    private PlanRunReason? SameFile(string first, string second) => _fs.SameFile(first, second).Answer switch
    {
        SameFileAnswer.Different => null,
        SameFileAnswer.Same => PlanRunReason.SameFile,
        _ => PlanRunReason.IdentityUnknown
    };

    private async Task<string?> HashAsync(string path, CancellationToken ct)
    {
        var stat = _hasher.Stat(path);
        if (!stat.Exists) return null;
        try
        {
            return await _hasher.ComputeFullAsync(path, stat.Size, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string TargetOf(PlanAction a, SourceFile f) =>
        a.SourceKind == PlanSourceKind.Folder ? PlanChecks.Join(a.TargetPath!, f.Relative) : a.TargetPath!;

    private static string HoldPath(RunState run, PlanAction a, string source) =>
        PlanChecks.Join(run.Job.RootPath, HoldFolderName, $"plan-{run.Plan.Id}", $"action-{a.Id}", Path.GetRelativePath(run.Job.RootPath, source));

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Source folders left empty are only reported, never removed. A move notice in them does not count.</summary>
    private static void NoteEmptyFolders(RunState run, string folder, ISet<string> emptied)
    {
        for (var dir = folder; dir is not null && PlanChecks.IsAtOrBelow(dir, run.Job.RootPath); dir = Path.GetDirectoryName(dir))
        {
            if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any(e => !IsNotice(e))) break;
            emptied.Add(dir);
        }
    }

    /// <summary>Removes the empty folders of the hold folder of this plan; a folder with a held original stays.</summary>
    private static void CleanHoldFolder(RunState run)
    {
        var top = PlanChecks.Join(run.Job.RootPath, HoldFolderName);
        var planFolder = PlanChecks.Join(top, $"plan-{run.Plan.Id}");
        RemoveEmptyBelow(planFolder);
        foreach (var dir in new[] { planFolder, top })
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void RemoveEmptyBelow(string folder)
    {
        if (!Directory.Exists(folder) || new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
        foreach (var sub in Directory.EnumerateDirectories(folder).ToList())
        {
            RemoveEmptyBelow(sub);
            try
            {
                if (!Directory.EnumerateFileSystemEntries(sub).Any())
                    Directory.Delete(sub, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<PlanStep> StartAsync(RunState run, PlanAction a, PlanStepKind kind, string source, string? target, string? hold,
        long bytes, string? hash, CancellationToken ct)
    {
        var step = await _journal.StartAsync(new PlanStep
        {
            PlanId = run.Plan.Id, ActionId = a.Id, Kind = kind, SourcePath = source, TargetPath = target, HoldPath = hold,
            Bytes = bytes, Hash = hash, StartedAt = _time.GetUtcNow().UtcDateTime
        }, ct);
        run.Steps.Add(step);
        return step;
    }

    private async Task FinishAsync(RunState run, PlanStep step, PlanStepState state, string? detail, CancellationToken ct)
    {
        step.State = state;
        step.Detail = detail;
        step.FinishedAt = _time.GetUtcNow().UtcDateTime;
        // A step settled after a cut-off run is written whatever the token says: the journal must not stay open.
        await _journal.FinishAsync(step, CancellationToken.None);
    }

    private async Task<PlanRunItem> FailAsync(RunState run, PlanAction a, PlanRunReason reason, string detail, long files, long bytes, CancellationToken ct)
    {
        if (!run.Execute)
            return Item(a, "would_fail", [reason.ToCode()], files, bytes, detail);
        a.State = PlanActionState.Failed;
        a.Note = $"{reason.ToCode()}: {detail} The original stays.";
        await SaveAsync(a, ct);
        return Item(a, "failed", [reason.ToCode()], files, bytes, detail);
    }

    private async Task SaveAsync(PlanAction a, CancellationToken ct)
    {
        a.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await _repo.UpdateActionsAsync([a], CancellationToken.None);
    }

    private static PlanRunItem Item(PlanAction a, string outcome, IReadOnlyList<string> reasons, long files, long bytes, string? detail = null) =>
        new(a.Id, a.Kind.ToCode(), outcome, a.State.ToCode(), reasons, files, bytes, detail);

    private static string? Join(IReadOnlyCollection<string> parts) => parts.Count == 0 ? null : string.Join("; ", parts);

    private static PlanRunReport Report(RunState run, string mode, IReadOnlyList<PlanRunItem> items, IReadOnlyList<string> emptied,
        IReadOnlyList<PlanNoticeItem>? notices = null)
    {
        // What --remove-sources would take away now: the originals of every verified action not yet held.
        var waiting = run.Actions.Where(a => a.State == PlanActionState.Verified).ToList();
        var files = waiting.SelectMany(a => run.Checked(a.Id).Where(s => run.ActiveHold(a.Id, s.SourcePath) is null)).ToList();
        return new PlanRunReport(run.Plan.Id, mode, run.Execute, run.KeepSource, items,
            new PlanRemovalSummary(waiting.Count, files.Count, files.Sum(s => s.Bytes)), emptied, notices ?? []);
    }

    // ------------------------------------------------------------------ move notices

    /// <summary>
    /// Brings the move notice of every source folder this plan took originals from in line with the journal
    /// (https://github.com/crack666/smart_file_organizer/issues/86): it lists the originals of this plan that are away
    /// (held or purged, not put back), beside the entries of other plans, which stay as they are. A notice without
    /// entries is removed. With <paramref name="create"/> false only existing notices are changed (undo; a run
    /// without notices). A file with the notice name that is not a notice is never touched. Each change is a journal
    /// step, written as a temporary file and then named, so a cut-off run leaves the old notice or the new one.
    /// </summary>
    private async Task<IReadOnlyList<PlanNoticeItem>> UpdateNoticesAsync(RunState run, bool create, CancellationToken ct)
    {
        var result = new List<PlanNoticeItem>();
        // A dry run counts the originals it would take away and leaves out those it would put back.
        var holds = run.Steps.Where(s => s.Kind == PlanStepKind.Hold && s.State == PlanStepState.Done).Concat(run.WouldHold).ToList();
        bool Away(PlanStep h) => !run.WouldReturn.Contains(h.ActionId)
                                 && (h.Id == 0 || run.ActiveHold(h.ActionId, h.SourcePath)?.Id == h.Id);
        foreach (var folder in holds.Select(h => Path.GetDirectoryName(h.SourcePath)!).Distinct(PathComparer).Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(folder, MoveNotice.FileName);
            var away = holds
                .Where(h => PathComparer.Equals(Path.GetDirectoryName(h.SourcePath), folder) && Away(h))
                .Select(h => new MoveNoticeEntry(h.FinishedAt ?? h.StartedAt, run.Plan.Id, h.ActionId, Path.GetFileName(h.SourcePath),
                    h.TargetPath ?? string.Empty))
                .ToList();

            MoveNoticeDocument? existing = null;
            var exists = File.Exists(path);
            if (Directory.Exists(path))
            {
                result.Add(new PlanNoticeItem(path, "skipped", 0, "A folder carries the name of the notice."));
                continue;
            }
            if (exists)
            {
                existing = ReadNotice(path);
                if (existing is not { Readable: true })
                {
                    result.Add(new PlanNoticeItem(path, "skipped", 0,
                        existing is null ? "A file that is not a move notice carries its name; it is left alone."
                                         : $"A move notice of format {existing.Version} lies there; it is left alone."));
                    continue;
                }
            }
            else if (!create || away.Count == 0 || !Directory.Exists(folder))
            {
                continue;
            }

            var others = existing?.Entries.Where(e => e.PlanId != run.Plan.Id) ?? [];
            var entries = MoveNotice.Order(others.Concat(away)).ToList();
            var text = MoveNotice.Render(entries);
            if (existing is not null && MoveNotice.Render(existing.Entries) == text)
                continue;
            if (!run.Execute)
            {
                result.Add(new PlanNoticeItem(path, entries.Count == 0 ? "would_remove" : "would_write", entries.Count, null));
                continue;
            }

            var actionId = away.Select(e => e.ActionId).Concat(existing?.Entries.Where(e => e.PlanId == run.Plan.Id).Select(e => e.ActionId) ?? [])
                .Concat(holds.Where(h => PathComparer.Equals(Path.GetDirectoryName(h.SourcePath), folder)).Select(h => h.ActionId))
                .First();
            var step = await StartAsync(run, run.Actions.Single(a => a.Id == actionId), PlanStepKind.Notice, path, null, null, entries.Count, null, ct);
            try
            {
                if (entries.Count == 0)
                {
                    File.Delete(path);
                    await FinishAsync(run, step, PlanStepState.Done, "No entry left; the notice was removed.", ct);
                    result.Add(new PlanNoticeItem(path, "removed", 0, null));
                    continue;
                }
                var partial = path + PartialSuffix;
                TryDeleteFile(partial);
                await File.WriteAllTextAsync(partial, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
                // Replaced only when what lies there is the notice read above; a new file is never put over another.
                File.Move(partial, path, overwrite: exists);
                await FinishAsync(run, step, PlanStepState.Done, $"{entries.Count} entries.", ct);
                result.Add(new PlanNoticeItem(path, "written", entries.Count, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteFile(path + PartialSuffix);
                await FinishAsync(run, step, PlanStepState.Failed, $"{PlanRunReason.IoError.ToCode()}: {ex.Message}", ct);
                result.Add(new PlanNoticeItem(path, "failed", existing?.Entries.Count ?? 0, ex.Message));
            }
        }
        return result;
    }

    /// <summary>Reads a file that carries the notice name; null when it is not a notice or cannot be read.</summary>
    private static MoveNoticeDocument? ReadNotice(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MoveNotice.MaxBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return null;
            return MoveNotice.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="path"/> is a move notice (by name and first line), which is never content.</summary>
    private static bool IsNotice(string path) => MoveNotice.HasNoticeName(path) && ReadNotice(path) is not null;
}
