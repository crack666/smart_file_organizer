namespace SmartFileOrganizer.Domain.Models;

/// <summary>A scanned file whose size occurs more than once among the jobs of a duplicate report (stage 1).</summary>
public sealed record DuplicateCandidate(long JobId, ComparableFile File);

/// <summary>Files of one folder of a job (not below it), counted for the folder summary of a duplicate report.</summary>
public sealed record FolderFileCount(long JobId, string Path, int Files, long Bytes);
