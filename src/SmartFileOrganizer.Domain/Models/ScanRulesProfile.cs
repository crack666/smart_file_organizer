namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// The scan rules a job was scanned with, as stored at the job: the profile name, where it came from
/// (a file path or <c>built-in</c>), a hash of the normalised rules and the normalised rules as JSON.
/// Keeping the rules themselves, not only the path, lets a job be resumed with exactly its rules
/// even after the file was edited or moved.
/// </summary>
public sealed record ScanRulesProfile(string Name, string Source, string Hash, string Json);
