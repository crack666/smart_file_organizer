using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>The model calls of the three phases.</summary>
/// <remarks>
/// Failures come in two kinds (https://github.com/crack666/smart_file_organizer/issues/63). The server answered
/// and the item could not be judged (rejected request, unreadable answer, every attempt timed out while the server
/// itself answers): the call returns a result with <c>Error</c> set, and the caller stores it at the item. The
/// server did not answer at all after the retries: the call throws <see cref="ModelServerUnavailableException"/>
/// and the caller stores nothing. Cancelling the token always throws <see cref="OperationCanceledException"/>.
/// </remarks>
public interface IOllamaService
{
    /// <summary>
    /// Classify a file using the Ollama model.
    /// Returns a classification result. On failure, returns a result with Error set.
    /// </summary>
    Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default);

    /// <summary>
    /// Model and prompt version of <see cref="ClassifyAsync"/>, the key under which its answers are kept in the content
    /// store (https://github.com/crack666/smart_file_organizer/issues/38). Null: the answers are not kept and nothing is
    /// taken over, as for scripted answers.
    /// </summary>
    AssessmentIdentity? AssessmentIdentity => null;

    /// <summary>
    /// Check whether the configured Ollama endpoint is reachable. False when it does not answer;
    /// cancelling <paramref name="ct"/> throws.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether the server answers and offers the configured model: the check before a waiting run continues.
    /// Cancelling <paramref name="ct"/> throws.
    /// </summary>
    Task<bool> IsReadyAsync(CancellationToken ct = default) => IsAvailableAsync(ct);

    /// <summary>
    /// List local models available on the configured Ollama server.
    /// </summary>
    Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Load the specified model into memory and keep it warm using the configured keep_alive setting.
    /// </summary>
    Task WarmModelAsync(string? model = null, CancellationToken ct = default);

    /// <summary>
    /// Phase 1: cheap text-only LLM call that judges a directory (verdict, purpose, retention, reason)
    /// from its file list and profile, and recommends a sampling strategy. On failure both parts carry the error.
    /// </summary>
    Task<DirectoryPreAssessmentOutcome> PreAssessDirectoryAsync(DirectoryPreAssessmentInput input, CancellationToken ct = default);

    /// <summary>Phase 3: summarize a directory bottom-up from analyzed file results.</summary>
    Task<DirectoryClassificationResult> SummarizeDirectoryAsync(DirectorySummaryInput input, CancellationToken ct = default);

    /// <summary>
    /// Whether the endpoint answered, and whether that answer was a refusal (HTTP 429 or 503).
    /// A stand-in that only implements <see cref="IsAvailableAsync"/> reports not busy.
    /// </summary>
    async Task<ModelEndpointProbe> ProbeEndpointAsync(CancellationToken ct = default)
    {
        var ok = await IsAvailableAsync(ct);
        return new ModelEndpointProbe(ok, false);
    }

    /// <summary>
    /// How many short calls the server answered at once, or null when it could not be measured.
    /// A stand-in that does not speak HTTP returns null.
    /// </summary>
    Task<int?> MeasureParallelismAsync(int requested, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);
}

/// <summary>Result of one cheap reachability check of the model server.</summary>
/// <param name="Reachable">The server answered, including a refusal.</param>
/// <param name="Busy">The answer was HTTP 429 or 503.</param>
public readonly record struct ModelEndpointProbe(bool Reachable, bool Busy);
