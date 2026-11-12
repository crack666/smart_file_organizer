using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Interfaces;

public interface IClassificationInputPreparer
{
    Task<OllamaClassificationInput> PrepareAsync(FileNode file, CancellationToken ct = default);

    /// <summary>Evidence at the given depth. Preparers without an extended mode return the standard evidence.</summary>
    Task<OllamaClassificationInput> PrepareAsync(FileNode file, EvidenceDepth depth, CancellationToken ct = default) =>
        PrepareAsync(file, ct);
}
