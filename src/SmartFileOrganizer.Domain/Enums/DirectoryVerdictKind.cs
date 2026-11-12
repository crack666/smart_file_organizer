using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// What a directory is, and therefore whether it can be handled as a whole.
/// The code in <see cref="JsonStringEnumMemberNameAttribute"/> is the fixed value stored in the
/// database and written to JSON; see <see cref="VerdictCodes"/>.
/// </summary>
public enum DirectoryVerdictKind
{
    /// <summary>Project, program, game or backup that only makes sense as a whole; never split up file by file.</summary>
    [JsonStringEnumMemberName("einheit")] Einheit,

    /// <summary>Collection of similar content (photos of one trip, invoices of one year).</summary>
    [JsonStringEnumMemberName("einheitlich")] Einheitlich,

    /// <summary>Storage without a common purpose (desktop, downloads, misc).</summary>
    [JsonStringEnumMemberName("gemischt")] Gemischt,

    /// <summary>Contains no files, neither directly nor in subdirectories.</summary>
    [JsonStringEnumMemberName("leer")] Leer,

    /// <summary>Operating system, tool or cache directory that is not user content.</summary>
    [JsonStringEnumMemberName("system")] System
}

/// <summary>How worth keeping the content of a directory is.</summary>
public enum RetentionValue
{
    [JsonStringEnumMemberName("behalten")] Behalten,
    [JsonStringEnumMemberName("pruefen")] Pruefen,
    [JsonStringEnumMemberName("entbehrlich")] Entbehrlich
}

/// <summary>Who decided a directory verdict.</summary>
public enum VerdictSource
{
    /// <summary>A deterministic rule decided without calling the model.</summary>
    [JsonStringEnumMemberName("regel")] Regel,

    /// <summary>The model decided (or was asked and failed).</summary>
    [JsonStringEnumMemberName("modell")] Modell
}
