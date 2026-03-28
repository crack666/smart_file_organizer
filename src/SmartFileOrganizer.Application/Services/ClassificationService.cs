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
    private readonly IOllamaService _ollama;
    private readonly ILogger<ClassificationService> _logger;

    private const int BatchSize = 10;

    public ClassificationService(
        IFileRepository fileRepo,
        IClassificationRepository classRepo,
        IOllamaService ollama,
        ILogger<ClassificationService> logger)
    {
        _fileRepo = fileRepo;
        _classRepo = classRepo;
        _ollama = ollama;
        _logger = logger;
    }

    /// <summary>
    /// Processes pending files for AI classification until cancelled or no more pending.
    /// </summary>
    public async Task RunAsync(long jobId, CancellationToken ct)
    {
        if (!await _ollama.IsAvailableAsync(ct))
        {
            _logger.LogWarning("Ollama is not available. Skipping AI classification.");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            var batch = await _fileRepo.GetPendingAiAnalysisAsync(jobId, BatchSize, ct);
            if (batch.Count == 0) break;

            foreach (var file in batch)
            {
                ct.ThrowIfCancellationRequested();

                _logger.LogDebug("Classifying: {Path}", file.FullPath);

                var result = await _ollama.ClassifyAsync(file, ct);
                result.FileNodeId = file.Id;

                await _classRepo.UpsertAsync(result, ct);

                var newStatus = result.Error != null
                    ? FileNodeStatus.Error
                    : FileNodeStatus.AiAnalyzed;

                await _fileRepo.UpdateStatusAsync(file.Id, newStatus, ct);

                _logger.LogDebug(
                    "Classified {Path}: {Category} (confidence: {Confidence:P0})",
                    file.FullPath, result.Category, result.Confidence);
            }
        }
    }
}
