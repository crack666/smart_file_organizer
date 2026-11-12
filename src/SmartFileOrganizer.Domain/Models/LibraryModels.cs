using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>One file as the library review needs it: where it lies and what the scan recorded, no content.</summary>
public sealed class StructureFileRow
{
    public string ParentPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public int FileType { get; set; }
    public string Extension { get; set; } = string.Empty;
    public DateTime LastWriteTime { get; set; }
    public SensitiveMarker SensitiveMarkers { get; set; }
}

/// <summary>Which question the model is asked about a job.</summary>
public enum LibraryAdviceTask
{
    /// <summary>Improve an existing library: roles, what is good, changes, target outline.</summary>
    Review,

    /// <summary>No library yet: propose a target structure for what a source holds.</summary>
    Propose
}

/// <summary>A question to the local model. <see cref="Facts"/> is the compact description built from scan data.</summary>
public sealed record LibraryAdviceRequest(LibraryAdviceTask Task, string Facts, string? Model, string? Language);

/// <summary>
/// The model's answer as received: <see cref="Content"/> is the JSON text the model wrote, parsed by the caller.
/// On failure <see cref="Error"/> is set and <see cref="Content"/> is null.
/// </summary>
public sealed record LibraryAdviceReply(string? Content, string Model, string? RawResponse, string? Error);

/// <summary>A stored answer, keyed by job, task and a hash of the facts it was asked about.</summary>
public sealed class LibraryAdviceRecord
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public string Task { get; set; } = string.Empty;
    public string InputHash { get; set; } = string.Empty;
    public string? Model { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Content { get; set; }
    public string? RawResponse { get; set; }
    public string? Error { get; set; }
}
