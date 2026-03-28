using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IOllamaService
{
    /// <summary>
    /// Classify a file using the Ollama model.
    /// Returns a classification result. On failure, returns a result with Error set.
    /// </summary>
    Task<FileClassification> ClassifyAsync(FileNode file, CancellationToken ct = default);

    /// <summary>
    /// Check whether the configured Ollama endpoint is reachable.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
