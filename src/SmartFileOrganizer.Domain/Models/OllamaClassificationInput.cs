namespace SmartFileOrganizer.Domain.Models;

public class OllamaClassificationInput
{
    public FileNode File { get; init; } = null!;
    public string ExtractedText { get; init; } = string.Empty;
    public IReadOnlyList<string> Base64Images { get; init; } = [];

    /// <summary>Ask the model whether the file is temporary or worth keeping (files of mixed or uniform directories).</summary>
    public bool AskKeepHint { get; set; }
}