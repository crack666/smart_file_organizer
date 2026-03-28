using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Orchestrates AI classification for file nodes.
/// Picks up discovered files in batches and sends them to Ollama.
/// </summary>
public class ClassificationService
{
    private readonly IFileRepository _fileRepo;
    private readonly IClassificationRepository _classRepo;
    private readonly IClassificationInputPreparer _inputPreparer;
    private readonly IOllamaService _ollama;
    private readonly ILogger<ClassificationService> _logger;

    private const int BatchSize = 10;

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

    /// <summary>
    /// Processes pending files for AI classification until cancelled or no more pending.
    /// </summary>
    public async Task<AiClassificationRunResult> RunAsync(
        long jobId,
        IProgress<AiClassificationProgress>? progress = null,
        Func<CancellationToken, Task>? waitIfPausedAsync = null,
        CancellationToken ct = default)
    {
        var totalEligible = await _fileRepo.CountAiEligibleAsync(jobId, ct);
        var pendingAtStart = await _fileRepo.CountPendingAiAnalysisAsync(jobId, ct);
        var processed = Math.Max(0, totalEligible - pendingAtStart);
        var errorCount = 0;

        if (totalEligible == 0)
        {
            const string noEligibleMessage = "No AI-eligible files found.";
            progress?.Report(new AiClassificationProgress(jobId, 0, 0, 0, string.Empty, noEligibleMessage));
            return new AiClassificationRunResult(true, 0, 0, 0, noEligibleMessage);
        }

        if (!await _ollama.IsAvailableAsync(ct))
        {
            const string unavailableMessage = "Ollama is not available. Skipping AI classification.";
            _logger.LogWarning(unavailableMessage);
            progress?.Report(new AiClassificationProgress(jobId, processed, totalEligible, 0, string.Empty, unavailableMessage));
            return new AiClassificationRunResult(false, totalEligible, processed, 0, unavailableMessage);
        }

        progress?.Report(new AiClassificationProgress(
            jobId,
            processed,
            totalEligible,
            errorCount,
            string.Empty,
            $"AI classifying… {processed:N0} / {totalEligible:N0}"));

        while (!ct.IsCancellationRequested)
        {
            if (waitIfPausedAsync != null)
                await waitIfPausedAsync(ct);

            var batch = await _fileRepo.GetPendingAiAnalysisAsync(jobId, BatchSize, ct);
            if (batch.Count == 0) break;

            foreach (var file in batch)
            {
                ct.ThrowIfCancellationRequested();

                if (waitIfPausedAsync != null)
                    await waitIfPausedAsync(ct);

                progress?.Report(new AiClassificationProgress(
                    jobId,
                    processed,
                    totalEligible,
                    errorCount,
                    file.FullPath,
                    $"AI classifying… {processed:N0} / {totalEligible:N0}"));

                _logger.LogDebug("Classifying: {Path}", file.FullPath);

                var input = await _inputPreparer.PrepareAsync(file, ct);
                var result = await _ollama.ClassifyAsync(input, ct);
                result.FileNodeId = file.Id;

                await _classRepo.UpsertAsync(result, ct);

                var newStatus = result.Error != null
                    ? FileNodeStatus.Error
                    : FileNodeStatus.AiAnalyzed;

                await _fileRepo.UpdateStatusAsync(file.Id, newStatus, ct);

                if (result.Error != null)
                    errorCount++;

                processed++;

                progress?.Report(new AiClassificationProgress(
                    jobId,
                    processed,
                    totalEligible,
                    errorCount,
                    file.FullPath,
                    $"AI classifying… {processed:N0} / {totalEligible:N0}"));

                _logger.LogDebug(
                    "Classified {Path}: {Category} (confidence: {Confidence:P0})",
                    file.FullPath, result.Category, result.Confidence);
            }
        }

        var completedMessage = errorCount > 0
            ? $"AI classification finished with {errorCount} error(s)."
            : "AI classification completed.";

        progress?.Report(new AiClassificationProgress(
            jobId,
            processed,
            totalEligible,
            errorCount,
            string.Empty,
            completedMessage));

        return new AiClassificationRunResult(true, totalEligible, processed, errorCount, completedMessage);
    }
}
