using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Orchestrates AI classification for file nodes using a producer-consumer pipeline.
/// Producer prepares file inputs (IO-bound) while workers send requests to Ollama (GPU-bound).
/// </summary>
public class ClassificationService
{
    private readonly IFileRepository _fileRepo;
    private readonly IClassificationRepository _classRepo;
    private readonly IClassificationInputPreparer _inputPreparer;
    private readonly IOllamaService _ollama;
    private readonly Func<int> _getMaxParallelRequests;
    private readonly ILogger<ClassificationService> _logger;

    /// <summary>How many files to fetch from DB per producer iteration.</summary>
    private const int FetchBatchSize = 30;

    /// <summary>
    /// Prepared inputs buffered ahead of Ollama workers.
    /// Small buffer keeps memory bounded while hiding IO latency.
    /// </summary>
    private const int ChannelCapacity = 6;

    public ClassificationService(
        IFileRepository fileRepo,
        IClassificationRepository classRepo,
        IClassificationInputPreparer inputPreparer,
        IOllamaService ollama,
        Func<int> getMaxParallelRequests,
        ILogger<ClassificationService> logger)
    {
        _fileRepo = fileRepo;
        _classRepo = classRepo;
        _inputPreparer = inputPreparer;
        _ollama = ollama;
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
        await _fileRepo.ResetStaleProcessingAsync(jobId, ct);
        var pendingAtStart = await _fileRepo.CountPendingAiAnalysisAsync(jobId, ct);

        long processed = Math.Max(0, totalEligible - pendingAtStart);
        var errorCount = 0;

        var workerCount = Math.Clamp(_getMaxParallelRequests(), 1, 8);
        var prepareParallelism = Math.Clamp(workerCount * 2, 2, 8);

        if (totalEligible == 0)
        {
            const string noEligibleMessage = "No AI-eligible files found.";
            progress?.Report(new AiClassificationProgress(jobId, 0, 0, 0, string.Empty, noEligibleMessage));
            return new AiClassificationRunResult(true, 0, 0, 0, noEligibleMessage);
        }

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

                var result = await _ollama.ClassifyAsync(input, ct);
                result.FileNodeId = file.Id;

                await _classRepo.UpsertAsync(result, ct);

                var newStatus = result.Error != null ? FileNodeStatus.Error : FileNodeStatus.AiAnalyzed;
                await _fileRepo.UpdateStatusAsync(file.Id, newStatus, ct);

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

        var completedMessage = errorCount > 0
            ? $"AI classification finished with {errorCount} error(s)."
            : "AI classification completed.";

        progress?.Report(new AiClassificationProgress(
            jobId,
            Interlocked.Read(ref processed),
            totalEligible,
            Volatile.Read(ref errorCount),
            string.Empty,
            completedMessage));

        return new AiClassificationRunResult(
            true,
            totalEligible,
            Interlocked.Read(ref processed),
            Volatile.Read(ref errorCount),
            completedMessage);
    }

    public async Task<FileClassification> ClassifySingleAsync(FileNode file, CancellationToken ct = default)
    {
        var input = await _inputPreparer.PrepareAsync(file, ct);
        var result = await _ollama.ClassifyAsync(input, ct);
        result.FileNodeId = file.Id;

        await _classRepo.UpsertAsync(result, ct);

        var newStatus = result.Error != null ? FileNodeStatus.Error : FileNodeStatus.AiAnalyzed;
        await _fileRepo.UpdateStatusAsync(file.Id, newStatus, ct);

        return result;
    }
}
