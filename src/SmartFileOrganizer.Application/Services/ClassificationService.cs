using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Phase 2: judges files with the model, in stages
/// (https://github.com/crack666/smart_file_organizer/issues/8).
/// Every file type takes part: files a rule can decide (programs, fonts, unknown binaries) are
/// stored without a model call (https://github.com/crack666/smart_file_organizer/issues/22).
/// </summary>
/// <remarks>
/// <para>1. <see cref="DirectorySamplingService.PlanAsync"/> draws the sample of every uniform directory;
/// the other files of such a directory wait as Deferred.</para>
/// <para>2. A producer-consumer pass judges every Discovered file: sample members, files of mixed
/// directories and files of directories without a verdict. The producer prepares inputs (IO-bound) while
/// workers send requests to Ollama (GPU-bound).</para>
/// <para>3. <see cref="DirectorySamplingService.ResolveAsync"/> lets the Deferred files of a confirmed
/// sample inherit, or raises the directory to mixed and releases its files; then another pass judges them.</para>
/// </remarks>
public class ClassificationService
{
    private readonly IFileRepository _fileRepo;
    private readonly IClassificationRepository _classRepo;
    private readonly IFileFactRepository _factRepo;
    private readonly IClassificationInputPreparer _inputPreparer;
    private readonly IOllamaService _ollama;
    private readonly DirectorySamplingService _sampling;
    private readonly Func<int> _getMaxParallelRequests;
    private readonly ILogger<ClassificationService> _logger;

    /// <summary>How many files to fetch from DB per producer iteration.</summary>
    private const int FetchBatchSize = 30;

    /// <summary>
    /// Prepared inputs buffered ahead of Ollama workers.
    /// Small buffer keeps memory bounded while hiding IO latency.
    /// </summary>
    private const int ChannelCapacity = 6;

    /// <summary>Plan → judge → resolve repeats while samples escalate; each escalation needs one more pass.
    /// Escalated directories have no further sample, so two passes suffice; the bound only guards a loop.</summary>
    private const int MaxPasses = 4;

    public ClassificationService(
        IFileRepository fileRepo,
        IClassificationRepository classRepo,
        IFileFactRepository factRepo,
        IClassificationInputPreparer inputPreparer,
        IOllamaService ollama,
        DirectorySamplingService sampling,
        Func<int> getMaxParallelRequests,
        ILogger<ClassificationService> logger)
    {
        _fileRepo = fileRepo;
        _classRepo = classRepo;
        _factRepo = factRepo;
        _inputPreparer = inputPreparer;
        _ollama = ollama;
        _sampling = sampling;
        _getMaxParallelRequests = getMaxParallelRequests;
        _logger = logger;
    }

    public async Task<AiClassificationRunResult> RunAsync(
        long jobId,
        IProgress<AiClassificationProgress>? progress = null,
        Func<CancellationToken, Task>? waitIfPausedAsync = null,
        CancellationToken ct = default)
    {
        var totalEligible = await _fileRepo.CountAiEligibleAsync(jobId, ct);
        if (totalEligible == 0)
        {
            const string noEligibleMessage = "No AI-eligible files found.";
            progress?.Report(new AiClassificationProgress(jobId, 0, 0, 0, string.Empty, noEligibleMessage));
            return new AiClassificationRunResult(true, 0, 0, 0, noEligibleMessage);
        }

        await _fileRepo.ResetStaleProcessingAsync(jobId, ct);
        await _sampling.PlanAsync(jobId, waitIfPausedAsync, ct);

        var errorCount = 0;
        var inherited = 0;
        for (var pass = 1; pass <= MaxPasses; pass++)
        {
            var context = await _sampling.GetContextAsync(jobId, ct);
            errorCount += await RunPassAsync(jobId, totalEligible, context, progress, waitIfPausedAsync, ct);

            var resolved = await _sampling.ResolveAsync(jobId, waitIfPausedAsync, ct);
            inherited += resolved.Inherited;
            if (resolved.Escalated == 0) break;
        }

        var pending = await _fileRepo.CountPendingAiAnalysisAsync(jobId, ct);
        var processed = totalEligible - pending;
        var inheritedText = inherited > 0 ? $" {inherited:N0} file(s) inherited from confirmed samples." : string.Empty;
        var completedMessage = errorCount > 0
            ? $"AI classification finished with {errorCount} error(s).{inheritedText}"
            : $"AI classification completed.{inheritedText}";

        progress?.Report(new AiClassificationProgress(jobId, processed, totalEligible, errorCount, string.Empty, completedMessage));
        return new AiClassificationRunResult(true, totalEligible, processed, errorCount, completedMessage);
    }

    /// <summary>Judges every Discovered eligible file once. Returns the number of errors.</summary>
    private async Task<int> RunPassAsync(
        long jobId,
        long totalEligible,
        DirectorySamplingService.Context context,
        IProgress<AiClassificationProgress>? progress,
        Func<CancellationToken, Task>? waitIfPausedAsync,
        CancellationToken ct)
    {
        var pendingAtStart = await _fileRepo.CountPendingAiAnalysisAsync(jobId, ct);
        if (pendingAtStart == 0) return 0;

        long processed = Math.Max(0, totalEligible - pendingAtStart);
        var errorCount = 0;

        var workerCount = Math.Clamp(_getMaxParallelRequests(), 1, 8);
        var prepareParallelism = Math.Clamp(workerCount * 2, 2, 8);

        progress?.Report(new AiClassificationProgress(
            jobId,
            processed,
            totalEligible,
            errorCount,
            string.Empty,
            $"AI classifying… {processed:N0} / {totalEligible:N0} (parallel={workerCount})"));

        var channel = Channel.CreateBounded<(FileNode File, OllamaClassificationInput Input)>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });

        var producerTask = Task.Run(async () =>
        {
            Exception? producerError = null;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (waitIfPausedAsync != null) await waitIfPausedAsync(ct);

                    var batch = await _fileRepo.GetPendingAiAnalysisAsync(jobId, FetchBatchSize, ct);
                    if (batch.Count == 0) break;

                    await _fileRepo.MarkAsProcessingAsync(batch.Select(f => f.Id).ToList(), ct);

                    await Parallel.ForEachAsync(
                        batch,
                        new ParallelOptions { MaxDegreeOfParallelism = prepareParallelism, CancellationToken = ct },
                        async (file, token) =>
                        {
                            if (waitIfPausedAsync != null) await waitIfPausedAsync(token);
                            var input = await _inputPreparer.PrepareAsync(file, token);
                            input.AskKeepHint = context.KeepHintDirectories.Contains(file.ParentPath);
                            await channel.Writer.WriteAsync((file, input), token);
                        });
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancel is requested.
            }
            catch (Exception ex)
            {
                producerError = ex;
                _logger.LogError(ex, "Producer failed for job {JobId}", jobId);
            }
            finally
            {
                channel.Writer.Complete(producerError);
            }
        }, ct);

        var workerTasks = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            await foreach (var (file, input) in channel.Reader.ReadAllAsync(ct))
            {
                if (waitIfPausedAsync != null) await waitIfPausedAsync(ct);

                var beforeProcessed = Interlocked.Read(ref processed);
                var beforeErrors = Volatile.Read(ref errorCount);
                progress?.Report(new AiClassificationProgress(
                    jobId,
                    beforeProcessed,
                    totalEligible,
                    beforeErrors,
                    file.FullPath,
                    $"AI classifying… {beforeProcessed:N0} / {totalEligible:N0} (parallel={workerCount})"));

                _logger.LogDebug("Classifying: {Path}", file.FullPath);

                var result = await CompleteAsync(file, input, ct);

                if (result.Error != null)
                    Interlocked.Increment(ref errorCount);

                var nowProcessed = Interlocked.Increment(ref processed);
                var nowErrors = Volatile.Read(ref errorCount);

                progress?.Report(new AiClassificationProgress(
                    jobId,
                    nowProcessed,
                    totalEligible,
                    nowErrors,
                    file.FullPath,
                    $"AI classifying… {nowProcessed:N0} / {totalEligible:N0} (parallel={workerCount})"));

                _logger.LogDebug(
                    "Classified {Path}: {Category} (confidence: {Confidence:P0})",
                    file.FullPath,
                    result.Category,
                    result.Confidence);
            }
        }, ct)).ToArray();

        await Task.WhenAll(workerTasks);
        await producerTask;

        return Volatile.Read(ref errorCount);
    }

    /// <summary>Judges one file with the model and stores the result (focused rescan, reanalysis).</summary>
    /// <param name="askKeepHint">Ask for the keep hint, as for files of mixed or uniform directories.</param>
    public async Task<FileClassification> ClassifySingleAsync(FileNode file, CancellationToken ct = default, bool askKeepHint = false)
    {
        var input = await _inputPreparer.PrepareAsync(file, ct);
        input.AskKeepHint = askKeepHint;
        return await CompleteAsync(file, input, ct);
    }

    /// <summary>
    /// Takes the rule result or asks the model, applies the sensitive review rule, and stores
    /// result, evidence notes and facts. Every file leaves here with a stored result: a rule
    /// result (<see cref="ResultOrigin.Rule"/>), a model result, or a model error. Rule results also
    /// set status <see cref="FileNodeStatus.AiAnalyzed"/>: Phase 2 is done for the file.
    /// </summary>
    private async Task<FileClassification> CompleteAsync(FileNode file, OllamaClassificationInput input, CancellationToken ct)
    {
        var result = input.RuleResult is { } rule
            ? CopyRuleResult(rule)
            : await _ollama.ClassifyAsync(input, ct);
        result.FileNodeId = file.Id;
        result.Origin = input.RuleResult is null ? ResultOrigin.Model : ResultOrigin.Rule;
        result.InheritedFromDirectoryNodeId = null;
        ApplySensitiveReviewRule(file, result);
        result.Evidence = FormatEvidence(input);

        // An answer the model already gave is kept even when the run is being stopped; otherwise the file
        // would fall back to Discovered and cost another call on resume.
        await _classRepo.UpsertAsync(result, CancellationToken.None);
        await _factRepo.ReplaceAsync(file.Id, FactsToStore(file, result, input.Facts), CancellationToken.None);

        var newStatus = result.Error != null ? FileNodeStatus.Error : FileNodeStatus.AiAnalyzed;
        await _fileRepo.UpdateStatusAsync(file.Id, newStatus, CancellationToken.None);
        return result;
    }

    private static FileClassification CopyRuleResult(FileClassification rule) => new()
    {
        Category = rule.Category,
        Importance = rule.Importance,
        Confidence = rule.Confidence,
        Summary = rule.Summary,
        SuggestedTarget = rule.SuggestedTarget,
        ModelUsed = rule.ModelUsed,
        AnalyzedAt = rule.AnalyzedAt == default ? DateTime.UtcNow : rule.AnalyzedAt,
        IsAiResult = false
    };

    /// <summary>
    /// For a file marked sensitive (by rule or by the model) free-text facts such as title or
    /// author are dropped: the marker stays the only statement about its content.
    /// </summary>
    public static IReadOnlyList<FileFact> FactsToStore(FileNode file, FileClassification result, IReadOnlyList<FileFact> facts)
    {
        var markers = SensitiveOutputGuard.GetMarkers(file) | result.SensitiveMarkers;
        return markers == SensitiveMarker.None ? facts : facts.Where(f => !f.Key.IsFreeText()).ToList();
    }

    /// <summary><c>{"contains":[...],"missing":[...]}</c>, or null when the evidence says nothing.</summary>
    public static string? FormatEvidence(OllamaClassificationInput input)
    {
        if (input.Contains.Count == 0 && input.Missing.Count == 0) return null;
        return JsonSerializer.Serialize(new { contains = input.Contains, missing = input.Missing });
    }

    /// <summary>
    /// A marked file is never stored as a trash candidate or as temporary: whatever the model proposes,
    /// it becomes "review needed" and "worth keeping". Markers come from the rules (scan time and file name)
    /// and from the model answer itself.
    /// </summary>
    public static void ApplySensitiveReviewRule(FileNode file, FileClassification result)
    {
        var markers = SensitiveOutputGuard.GetMarkers(file) | result.SensitiveMarkers;
        result.Category = SensitiveOutputGuard.ApplyReviewRule(result.Category, markers);
        if (markers != SensitiveMarker.None && result.KeepHint == KeepHint.Temporary)
        {
            result.KeepHint = KeepHint.WorthKeeping;
            // The model reason is dropped: it may describe the content of a sensitive file.
            result.KeepReason = $"Marked as sensitive ({SensitiveMarkerNames.Format(markers)}); never temporary.";
        }
    }
}
