namespace SmartFileOrganizer.Domain.Models;

/// <summary>Lightweight file metadata used during Phase 1 directory pre-assessment.</summary>
public class DirectoryFileInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public int FileType { get; set; }
    /// <summary>Lower-case extension including the dot, or empty.</summary>
    public string Extension { get; set; } = string.Empty;
    public DateTime LastWriteTime { get; set; }
}

public class DirectoryPreAssessmentInput
{
    public DirectoryNode Directory { get; init; } = null!;
    public IReadOnlyList<DirectoryFileInfo> DirectFiles { get; init; } = [];
    public IReadOnlyList<string> SubdirectoryNames { get; init; } = [];
    /// <summary>Facts computed without the model; sent along so the model judges from them, not from names alone.</summary>
    public DirectoryProfile Profile { get; init; } = new();
}

public class DirectorySummaryInput
{
    public DirectoryNode Directory { get; init; } = null!;
    public DirectoryClassificationResult PreAssessment { get; init; } = null!;
    /// <summary>One line per analyzed file: "filename.ext — Category — summary text"</summary>
    public IReadOnlyList<string> FileSummaryLines { get; init; } = [];
    /// <summary>One line per flagged anomaly: "filename.ext: reason"</summary>
    public IReadOnlyList<string> AnomalyDescriptions { get; init; } = [];
}
