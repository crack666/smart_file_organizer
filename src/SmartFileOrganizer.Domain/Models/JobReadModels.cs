using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// One directory of a job as the read commands list it: the scan row, the stored verdict codes and
/// the Phase 3 summary, in one query. Sizes and counts are direct; recursive totals are rolled up
/// by the caller from <see cref="DirectoryFileTotals"/>.
/// </summary>
public sealed class DirectoryReadRow
{
    public long Id { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ParentPath { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public int Depth { get; set; }
    public DirectoryStatus DirStatus { get; set; }
    public int SubdirCount { get; set; }

    /// <summary>Stored verdict code (<c>einheit</c>, ...); null when there is no verdict or only an error.</summary>
    public string? Verdict { get; set; }

    /// <summary><c>regel</c> or <c>modell</c>; null when Phase 1 has not reached the directory.</summary>
    public string? VerdictSource { get; set; }

    public string? VerdictRule { get; set; }
    public string? Retention { get; set; }
    public string? Purpose { get; set; }
    public string? Reason { get; set; }
    public string? VerdictError { get; set; }

    /// <summary>Phase 3 directory summary written by the model.</summary>
    public string? AiSummary { get; set; }

    public string? AiSummaryError { get; set; }

    /// <summary>Model that wrote the Phase 3 summary; null when it was built from the verdict.</summary>
    public string? AiSummaryModel { get; set; }

    /// <summary>Marked files that entered the Phase 3 summary as their neutral line (JSON list); null when unknown.</summary>
    public string? AiSummaryMarkedInputs { get; set; }

    /// <summary>Stored markers of a skipped folder (<see cref="DirectoryNode.SensitiveMarkers"/>).</summary>
    public SensitiveMarker SensitiveMarkers { get; set; }

    /// <summary>Database form of <see cref="SensitiveMarkers"/>; the persistence layer reads into it.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public string? SensitiveMarkerTokens
    {
        get => SensitiveMarkerNames.Format(SensitiveMarkers);
        set => SensitiveMarkers = SensitiveMarkerNames.Parse(value);
    }

    public string? SensitiveRule { get; set; }
}

/// <summary>Count and bytes of the files stored directly in one directory.</summary>
public sealed class DirectoryFileTotals
{
    public string ParentPath { get; set; } = string.Empty;
    public long Files { get; set; }
    public long Bytes { get; set; }
}

/// <summary>
/// The part of a file listing the database can filter cheaply. Everything the output guard can
/// change (category, confidence, sensitive markers) is filtered after projection.
/// </summary>
public sealed class FileListFilter
{
    public long JobId { get; init; }

    /// <summary>Only files directly in this directory (full path as stored); null for the whole job.</summary>
    public string? Directory { get; init; }

    /// <summary>With <see cref="Directory"/>: also files in its subdirectories.</summary>
    public bool Recursive { get; init; }

    /// <summary>Path prefix of a subdirectory of <see cref="Directory"/>, ending with a separator. Set with <see cref="Recursive"/>.</summary>
    public string? DirectoryPrefix { get; init; }

    /// <summary>Only files belonging directly to one of these bundles (pass a bundle with all its descendants). Empty: no filter.</summary>
    public IReadOnlyCollection<long> BundleIds { get; init; } = [];

    public IReadOnlyCollection<FileType> FileTypes { get; init; } = [];
    public IReadOnlyCollection<FileNodeStatus> Statuses { get; init; } = [];

    /// <summary>Order of the stream: by full path (default) or by size, largest first.</summary>
    public bool OrderBySize { get; init; }

    /// <summary>Load <see cref="FileClassification.RawResponse"/> as well. Off for listings.</summary>
    public bool IncludeRawResponse { get; init; }
}

/// <summary>
/// A file with its classification and override loaded, plus the verdict codes of its directory.
/// <see cref="DirectoryVerdict"/> is the verdict code, <c>error</c> when only an error is stored, or null.
/// </summary>
public sealed record FileReadRow(FileNode File, string? DirectoryVerdict, string? DirectoryRetention);
