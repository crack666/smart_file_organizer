using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

/// <summary>Storage behind <c>sfo library</c>. Reads scan data; writes only the model's advice.</summary>
public interface ILibraryRepository
{
    /// <summary>Every file of the job with the fields the structure rules use, in no particular order.</summary>
    IAsyncEnumerable<StructureFileRow> StreamStructureFilesAsync(long jobId, CancellationToken ct = default);

    /// <summary>The newest stored advice for this job, task and input hash, or null.</summary>
    Task<LibraryAdviceRecord?> GetAdviceAsync(long jobId, string task, string inputHash, CancellationToken ct = default);

    Task SaveAdviceAsync(LibraryAdviceRecord record, CancellationToken ct = default);
}

/// <summary>
/// Asks the local model about a library. Folder names can be private; implementations send them only to
/// the configured local Ollama server. Prompts live in the implementation, next to the other prompts.
/// </summary>
public interface ILibraryAdvisor
{
    Task<LibraryAdviceReply> AskAsync(LibraryAdviceRequest request, CancellationToken ct = default);
}
