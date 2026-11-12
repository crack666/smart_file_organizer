using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Models;

/// <summary>
/// One statement about a file that needs no model: read from the file's metadata or derived by
/// a rule. Stored in <c>file_facts</c>, one row per file and key. Booleans are <c>true</c>/<c>false</c>,
/// numbers use the invariant culture, dates ISO 8601, enum values their code.
/// </summary>
public sealed record FileFact(FileFactKey Key, string Value, FactSource Source = FactSource.Metadata);
