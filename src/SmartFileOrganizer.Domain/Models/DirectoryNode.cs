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

    /// <summary>
    /// Markers of a folder the scan skipped without entering (<c>.ssh</c>, <c>AppData</c>), so it can
    /// still show up as a finding. Stored as tokens in <c>directory_nodes.sensitive_markers</c>.
    /// </summary>
    public SensitiveMarker SensitiveMarkers { get; set; }

    /// <summary>Database form of <see cref="SensitiveMarkers"/> (token list); the persistence layer reads into it.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public string? SensitiveMarkerTokens
    {
        get => SensitiveMarkerNames.Format(SensitiveMarkers);
        set => SensitiveMarkers = SensitiveMarkerNames.Parse(value);
    }

    /// <summary>Ids of the rules that marked the folder (<c>dir:ssh</c>); no content.</summary>
    public string? SensitiveRule { get; set; }

    /// <summary>Code of <see cref="Enums.LinkKind"/> when the folder is a link: not followed
    /// (<see cref="DirectoryStatus.Link"/>) or followed by a <c>follow_link</c> rule. Null for a plain folder.</summary>
    public string? LinkKind { get; set; }

    /// <summary>Where the link points, as stored in it; null when unreadable or no link.</summary>
    public string? LinkTarget { get; set; }

    /// <summary>Code of <see cref="Enums.ScanErrorCause"/> when <see cref="DirStatus"/> is <see cref="DirectoryStatus.Error"/>.</summary>
    public string? ScanErrorCause { get; set; }
}
