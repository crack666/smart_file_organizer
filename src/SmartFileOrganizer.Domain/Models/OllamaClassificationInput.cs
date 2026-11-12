namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// The evidence for one file: what the model sees, the facts read without the model, and what
/// the evidence contains and lacks. When <see cref="RuleResult"/> is set, a rule has already
/// decided and the model is not called.
/// </summary>
public class OllamaClassificationInput
{
    public FileNode File { get; init; } = null!;
    public string ExtractedText { get; init; } = string.Empty;
    public IReadOnlyList<string> Base64Images { get; init; } = [];

<<<<<<< HEAD
    /// <summary>Ask the model whether the file is temporary or worth keeping (files of mixed or uniform directories).</summary>
    public bool AskKeepHint { get; set; }
}
=======
    /// <summary>Facts from metadata or rules. Stored as such and shown to the model as reliable.</summary>
    public IReadOnlyList<FileFact> Facts { get; init; } = [];

    /// <summary>What the evidence holds, in short English phrases ("page 1 rendered as image").</summary>
    public IReadOnlyList<string> Contains { get; init; } = [];

    /// <summary>What could not be provided and why ("no frame: no video tool found"), so the model does not guess.</summary>
    public IReadOnlyList<string> Missing { get; init; } = [];

    /// <summary>A result decided by a rule (executable, font, unknown binary). Set: no model call.</summary>
    public FileClassification? RuleResult { get; init; }
}
>>>>>>> 9c261f6 (B-5: Evidence per file type with facts and what is missing)
