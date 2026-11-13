using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>
/// Thrown by a model service (<see cref="IOllamaService"/> and any later provider) when a call got no answer from
/// the server after its retries: the server is away, not the item. The caller stores nothing for the item; it is
/// sent again once the server is back (https://github.com/crack666/smart_file_organizer/issues/63).
/// </summary>
/// <remarks>The message never contains prompt or answer text, so it may be logged on stderr.</remarks>
public sealed class ModelServerUnavailableException : Exception
{
    public ModelServerUnavailableException(ModelServerFailure failure, string message, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        RetryAfter = retryAfter;
    }

    public ModelServerFailure Failure { get; }

    /// <summary>The pause the server asked for (<c>Retry-After</c>), when it sent one.</summary>
    public TimeSpan? RetryAfter { get; }
}
