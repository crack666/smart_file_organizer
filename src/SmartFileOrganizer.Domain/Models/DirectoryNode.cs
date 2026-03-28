using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

public class DirectoryNode
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ParentPath { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public int Depth { get; set; }
    public DirectoryStatus DirStatus { get; set; } = DirectoryStatus.Pending;
    public string? DirReason { get; set; }

    // Aggregated statistics (populated after scan)
    public long TotalSize { get; set; }
    public int DirectFileCount { get; set; }
    public long RecursiveFileCount { get; set; }
    public int SubdirCount { get; set; }

    // JSON-serialised summary strings for dominant types/categories
    public string? DominantFileTypes { get; set; }
    public string? DominantCategories { get; set; }
    public string? SuggestedArea { get; set; }

    public DateTime ScannedAt { get; set; }
}
