using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

public class FileNode
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ParentPath { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string RelativeDir { get; set; } = string.Empty;
    public int Depth { get; set; }
    public long Size { get; set; }
    public DateTime LastWriteTime { get; set; }
    public string Extension { get; set; } = string.Empty;
    public FileType FileType { get; set; } = FileType.Unknown;
    public FileNodeStatus Status { get; set; } = FileNodeStatus.Discovered;
    public DateTime ScannedAt { get; set; }

    // Navigation / denormalized for sorting/filtering
    public FileClassification? Classification { get; set; }
    public UserOverride? Override { get; set; }
}
