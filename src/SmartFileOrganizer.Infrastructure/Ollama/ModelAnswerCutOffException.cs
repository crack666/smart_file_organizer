namespace SmartFileOrganizer.Infrastructure.Ollama;

/// <summary>
/// The model stopped its answer at the token limit (<c>num_predict</c>) on the call and on its one repetition.
/// Stored as the error of the file or directory; a cut-off answer is never parsed as a partial result.
/// </summary>
public sealed class ModelAnswerCutOffException(int maxTokens)
    : Exception($"The model answer was cut off at the limit of {maxTokens} tokens, twice.")
{
    public int MaxTokens { get; } = maxTokens;
}
