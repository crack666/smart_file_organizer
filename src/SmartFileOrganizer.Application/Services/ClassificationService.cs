using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Orchestrates AI classification for file nodes using a producer-consumer pipeline.
/// The producer fetches + prepares file inputs (IO-bound) concurrently with the consumer
/// sending requests to Ollama (GPU-bound), eliminating the idle gap between inferences.
/// </summary>
public class ClassificationService
{
    private readonly IFileRepository _fileRepo;
    private readonly IClassificationRepository _classRepo;
    private readonly IClassificationInputPreparer _inputPreparer;
    private readonly IOllamaService _ollama;
    private readonly ILogger<ClassificationService> _logger;

    /// <summary>How many files to fetch from DB per producer iteration.</summary>
    private const int FetchBatchSize = 30;

    /// <summary>
    /// Prepared inputs buffered ahead of the Ollama consumer.
    /// 4–6 is enough to hide all IO latency without wasting memory on large image batches.
    /// </summary>
    private const int ChannelCapacity = 6;

    public ClassificationService(
        IFileRepository fileRepo,
        IClassificationRepository classRepo,
        IClassificationInputPreparer inputPreparer,
        IOllamaService ollama,
        ILogger<ClassificationService> logger)
    {
        _fileRepo = fileRepo;
        _classRepo = classRepo;
        _inputPreparer = inputPreparer;
        _ollama = ollama;
        _logger = logger;
    }

    public async Task<AiClassificationRunResult> RunAsync(
        long jobId,
        IProgress<AiClassificationProgress>? progress = null,
        Func<CancellationToken, Task>? waitIfPausedAsync = null,
        CancellationToken ct = default)
    {
        var totalEligible = await _fileRepo.CountAiEligibleAsync(jobId, ct);
        // Reset any files stuck in Processing from a previous cancelled/crashed run
        await _fileRepo.ResetStaleProcessingAsync(jobId, ct);
        var pendingAtStart = await _fileRepo.CountPendingAiAnalysisAsync(jobId, ct);
        var processed = Math.Max(0, totalEligible - pendingAtStart);
        var errorCount = 0;

        if (totalEligible == 0)
        {
            const string noEligibleMessage = "No AI-eligible files found.";
            progress?.Report(new AiClassificationProgress(jobId, 0, 0, 0, string.Empty, noEligibleMessage));
            return new AiClassificationRunResult(true, 0, 0, 0, noEligibleMessage);
        }

        progress?.Report(new AiClassificationProgress(
            jobId, processed, totalEligible, errorCount, string.Empty,
            $"AI classifying\u2026 {processed:N0} / {totalEligible:N0}"));

        // Bounded channel: producer writes, consumer reads.
        // BoundedChannelFullMode.Wait makes producer block when consumer is busy — natural backpressure.
        var channel = Channel.CreateBounded<(FileNode File, OllamaClassificationInput Input)>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

        // ── Producer ──────────────────────────────────────────────────────────
        var producerTask = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (waitIfPausedAsync != null) await waitIfPausedAsync(ct);

                    var batch = await _fileRepo.GetPendingAiAnalysisAsync(jobId, FetchBatchSize, ct);
                    if (batch.Count == 0) break;

                    // Mark as Processing immediately so a resume run won't re-fetch these
                    await _fileRepo.MarkAsProcessingAsync(batch.Select(f => f.Id).ToList(), ct);

                    foreach (var file in batch)
                    {
                        ct.ThrowIfCancellationRequested();
                        var input = await _inputPreparer.PrepareAsync(file, ct);
                        await channel.Writer.WriteAsync((file, input), ct);
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on cancel */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Producer failed for job {JobId}", jobId);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        // ── Consumer (Ollama — one request at a time) ─────────────────────────
        await foreach (var (file, input) in channel.Reader.ReadAllAsync(ct))
        {
            if (waitIfPausedAsync != null) await waitIfPausedAsync(ct);

            progress?.Report(new AiClassificationProgress(
                jobId, processed, totalEligible, errorCount, file.FullPath,
                $"AI classifying\u2026 {processed:N0} / {totalEligible:N0}"));

            _logger.LogDebug("Classifying: {Path}", file.FullPath);

            var result = await _ollama.ClassifyAsync(input, ct);
            result.FileNodeId = file.Id;

            await _classRepo.UpsertAsync(result, ct);

            var newStatus = result.Error != null ? FileNodeStatus.Error : FileNodeStatus.AiAnalyzed;
            await _fileRepo.UpdateStatusAsync(file.Id, newStatus, ct);

            if (result.Error != null) errorCount++;
            processed++;

            progress?.Report(new AiClassificationProgress(
                jobId, processed, totalEligible, errorCount, file.FullPath,
                $"AI classifying\u2026 {processed:N0} / {totalEligible:N0}"));

            _logger.LogDebug(
                "Classified {Path}: {Category} (confidence: {Confidence:P0})",
                file.FullPath, result.Category, result.Confidence);
        }

        await producerTask; // propagate any producer exception

        var completedMessage = errorCount > 0
            ? $"AI classification finished with {errorCount} error(s)."
            : "AI classification completed.";

        progress?.Report(new AiClassificationProgress(
            jobId, processed, totalEligible, errorCount, string.Empty, completedMessage));

        return new AiClassificationRunResult(true, totalEligible, processed, errorCount, completedMessage);
    }
}
