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

    /// <summary>Markers set by the rules that need no model (name, extension, header line).
    /// Stored as tokens in <c>file_nodes.sensitive_markers</c>.</summary>
    public SensitiveMarker SensitiveMarkers { get; set; }

    /// <summary>Id of the rule that set <see cref="SensitiveMarkers"/>, for example
    /// <c>name:id_rsa</c> or <c>header:private-key</c>. Never contains file content.</summary>
    public string? SensitiveRule { get; set; }

    /// <summary>Stored form of <see cref="SensitiveMarkers"/>, for the database mapper only
    /// (it cannot convert enums itself; see <c>SensitiveMarkerColumnMap</c>).</summary>
    private string? SensitiveMarkerTokens
    {
        get => SensitiveMarkerNames.Format(SensitiveMarkers);
        set => SensitiveMarkers = SensitiveMarkerNames.Parse(value);
    }

    /// <summary>Version of the file rules that ran on this row (<c>SensitiveFileRules.Version</c>); null for rows
    /// scanned before versions were stored, whose header lines were possibly never checked.</summary>
    public int? SensitiveRulesVersion { get; set; }

    /// <summary>Code of the <see cref="Enums.ContentState"/> that let <c>sfo analyze --against</c> leave the file out
    /// (<c>backed_up</c>; status Skipped), or null when the file takes part in the analysis.</summary>
    public string? AnalysisSkip { get; set; }

    /// <summary>Code of <see cref="Enums.ScanErrorCause"/> when the scan listed the file but could not read its
    /// size or time; <see cref="Size"/> is then 0 and not a measurement.</summary>
    public string? ScanErrorCause { get; set; }

    /// <summary>The read error behind <see cref="ScanErrorCause"/>.</summary>
    public string? ScanError { get; set; }

    // Navigation / denormalized for sorting/filtering
    public FileClassification? Classification { get; set; }
    public UserOverride? Override { get; set; }
}
