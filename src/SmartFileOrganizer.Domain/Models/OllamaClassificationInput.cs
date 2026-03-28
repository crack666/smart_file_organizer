namespace SmartFileOrganizer.Domain.Models;

public class OllamaClassificationInput
{
    public FileNode File { get; init; } = null!;
    public string ExtractedText { get; init; } = string.Empty;
    public IReadOnlyList<string> Base64Images { get; init; } = [];
}