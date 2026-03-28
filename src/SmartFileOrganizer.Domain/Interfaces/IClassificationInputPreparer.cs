using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IClassificationInputPreparer
{
    Task<OllamaClassificationInput> PrepareAsync(FileNode file, CancellationToken ct = default);
}