using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IOllamaService
{
    /// <summary>
    /// Classify a file using the Ollama model.
    /// Returns a classification result. On failure, returns a result with Error set.
    /// </summary>
    Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default);

    /// <summary>
    /// Check whether the configured Ollama endpoint is reachable.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// List local models available on the configured Ollama server.
    /// </summary>
    Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Load the specified model into memory and keep it warm using the configured keep_alive setting.
    /// </summary>
    Task WarmModelAsync(string? model = null, CancellationToken ct = default);
}
