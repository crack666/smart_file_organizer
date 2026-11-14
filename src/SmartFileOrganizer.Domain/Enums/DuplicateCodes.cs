using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

/// <summary>
/// How sure a group of <c>sfo duplicates</c> is (https://github.com/crack666/smart_file_organizer/issues/87).
/// </summary>
public enum DuplicateCertainty
{
    /// <summary>Same size and same sample checksum; the whole files were not compared.</summary>
    [JsonStringEnumMemberName("likely")] Likely,

    /// <summary>Same full SHA-256 of every file of the group.</summary>
    [JsonStringEnumMemberName("certain")] Certain
}

/// <summary>What a group of <c>sfo duplicates</c> holds.</summary>
public enum DuplicateScope
{
    /// <summary>Files with the same content.</summary>
    [JsonStringEnumMemberName("files")] Files,

    /// <summary>Folders that are wholly or mostly the same; their identical files are not listed one by one.</summary>
    [JsonStringEnumMemberName("folders")] Folders
}

/// <summary>
/// What a group of <c>sfo duplicates</c> means for the person. The command only reports; it never removes anything and
/// never proposes to remove a copy inside a library.
/// </summary>
public enum DuplicateAdvice
{
    /// <summary>Only the sample checksum agrees: run again with <c>--verify</c> before relying on it.</summary>
    [JsonStringEnumMemberName("verify_first")] VerifyFirst,

    /// <summary>Every copy lies in a library: reported, nothing is proposed.</summary>
    [JsonStringEnumMemberName("library_only")] LibraryOnly,

    /// <summary>A library holds the content and copies lie in sources: those may go by the usual way (compare, findings, plan).</summary>
    [JsonStringEnumMemberName("source_copies_held")] SourceCopiesHeld,

    /// <summary>Copies lie in sources only and no library holds the content: nothing is removed without a library.</summary>
    [JsonStringEnumMemberName("no_library_copy")] NoLibraryCopy
}
