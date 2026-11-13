using System.Text.Json.Serialization;

namespace SmartFileOrganizer.Domain.Enums;

// Fixed codes of the library review (https://github.com/crack666/smart_file_organizer/issues/29).
// English, as decided in https://github.com/crack666/smart_file_organizer/issues/35; read and
// written through VerdictCodes (ToCode, TryParse), so JSON, database and CLI share one spelling.

/// <summary>What a rule found in the structure of a library. One finding bundles all cases under one parent.</summary>
public enum LibraryFindingKind
{
    /// <summary>A device, computer or program dump inside the library (phone storage, profile copy, unpacked package). Reported once and left out of every other rule.</summary>
    [JsonStringEnumMemberName("dump")] Dump,

    /// <summary>Sibling folders are named after different schemes (date in front, at the end, missing, in several formats).</summary>
    [JsonStringEnumMemberName("naming_inconsistent")] NamingInconsistent,

    /// <summary>The same level is spelled in several ways ("Season 1", "S01", a download name).</summary>
    [JsonStringEnumMemberName("variant_spellings")] VariantSpellings,

    /// <summary>Folders hold many files flat where most of their siblings are divided into subfolders.</summary>
    [JsonStringEnumMemberName("flat_among_structured")] FlatAmongStructured,

    /// <summary>Loose files at the top of the library, next to folders.</summary>
    [JsonStringEnumMemberName("loose_files")] LooseFiles,

    /// <summary>Folders with the same normalised name at several places (same purpose, kept twice). A candidate list, not a judgement.</summary>
    [JsonStringEnumMemberName("same_name_elsewhere")] SameNameElsewhere,

    /// <summary>Nearly equal names ("Holiday Italy", "Italy Holiday 2").</summary>
    [JsonStringEnumMemberName("similar_names")] SimilarNames,

    /// <summary>Subtrees with the same number of files and the same byte total at different places.</summary>
    [JsonStringEnumMemberName("likely_copy")] LikelyCopy,

    /// <summary>Empty folders or branches, also those holding only system files (Thumbs.db, .DS_Store).</summary>
    [JsonStringEnumMemberName("empty")] Empty,

    /// <summary>Placeholder or catch-all names ("New folder", "misc", "old"), with what has grown inside.</summary>
    [JsonStringEnumMemberName("placeholder_name")] PlaceholderName,

    /// <summary>Folders still named by the camera (<c>100CANON</c>).</summary>
    [JsonStringEnumMemberName("camera_folder_name")] CameraFolderName,

    /// <summary>Saved web pages: a <c>*_files</c> folder next to the <c>.htm</c> file of the same name.</summary>
    [JsonStringEnumMemberName("saved_web_page")] SavedWebPage,

    /// <summary>The file types do not fit the folder's name or the area it lies in (documents under photos).</summary>
    [JsonStringEnumMemberName("type_mismatch")] TypeMismatch,

    /// <summary>A single file holds more than half of the library's bytes.</summary>
    [JsonStringEnumMemberName("dominant_file")] DominantFile,

    /// <summary>A folder with very many files directly in it.</summary>
    [JsonStringEnumMemberName("very_full")] VeryFull,

    /// <summary>A branch far deeper than the rest of the library.</summary>
    [JsonStringEnumMemberName("very_deep")] VeryDeep,

    /// <summary>Credential, key or identity files. Reported with path and marker only.</summary>
    [JsonStringEnumMemberName("sensitive_location")] SensitiveLocation,

    /// <summary>A name starts in another letter case than most of its siblings.</summary>
    [JsonStringEnumMemberName("case_inconsistent")] CaseInconsistent
}

/// <summary>How much a finding matters; findings are ordered by it.</summary>
public enum FindingImpact
{
    [JsonStringEnumMemberName("high")] High,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("low")] Low
}

/// <summary>What kind of dump a <see cref="LibraryFindingKind.Dump"/> is.</summary>
public enum DumpKind
{
    /// <summary>Phone, camera or memory card storage (<c>Android</c>, <c>DCIM</c>, <c>LOST.DIR</c>, app package names).</summary>
    [JsonStringEnumMemberName("device")] Device,

    /// <summary>Copy of a computer or user profile (<c>Windows</c>, <c>Program Files</c>, <c>AppData</c>, recycle bin).</summary>
    [JsonStringEnumMemberName("computer")] Computer,

    /// <summary>A project, program or unpacked package (<c>.git</c>, <c>node_modules</c>, <c>.venv</c>, exe next to dll).</summary>
    [JsonStringEnumMemberName("package")] Package
}

/// <summary>The role of a folder, as the model judges it. Siblings with the same role are comparable.</summary>
public enum FolderRole
{
    [JsonStringEnumMemberName("occasion")] Occasion,
    [JsonStringEnumMemberName("person")] Person,
    [JsonStringEnumMemberName("device")] Device,
    [JsonStringEnumMemberName("topic")] Topic,
    [JsonStringEnumMemberName("time")] Time,
    [JsonStringEnumMemberName("media_title")] MediaTitle,
    [JsonStringEnumMemberName("season")] Season,
    [JsonStringEnumMemberName("document_type")] DocumentType,
    [JsonStringEnumMemberName("project")] Project,
    [JsonStringEnumMemberName("collection")] Collection,
    [JsonStringEnumMemberName("catch_all")] CatchAll,
    [JsonStringEnumMemberName("dump")] Dump,
    [JsonStringEnumMemberName("mixed")] Mixed
}

/// <summary>A change the model proposes for a library. Proposals are data; nothing is carried out here.</summary>
public enum ProposalAction
{
    [JsonStringEnumMemberName("rename")] Rename,
    [JsonStringEnumMemberName("move")] Move,
    [JsonStringEnumMemberName("merge")] Merge,
    [JsonStringEnumMemberName("split")] Split,
    [JsonStringEnumMemberName("create")] Create
}

/// <summary>Who produced a target structure or a piece of advice.</summary>
public enum AdviceOrigin
{
    [JsonStringEnumMemberName("rule")] Rule,
    [JsonStringEnumMemberName("model")] Model
}

// Codes of the target proposals (https://github.com/crack666/smart_file_organizer/issues/25).

/// <summary>Where <c>sfo library match</c> proposes to put something of a source. Proposals are data; nothing is moved.</summary>
public enum MatchPlacement
{
    /// <summary>The files go into an existing folder of the library.</summary>
    [JsonStringEnumMemberName("into_existing")] IntoExisting,

    /// <summary>A named source folder goes, whole and under its own name, into an existing folder next to its kind.</summary>
    [JsonStringEnumMemberName("as_subfolder")] AsSubfolder,

    /// <summary>No existing folder fits: a new folder, named in the style of its siblings.</summary>
    [JsonStringEnumMemberName("new_folder")] NewFolder
}

/// <summary>A signal behind a target proposal. Each is computed from scan data and the comparison, never from a model.</summary>
public enum MatchSignal
{
    /// <summary>Files with exactly the same content already lie there (from <c>sfo compare</c>).</summary>
    [JsonStringEnumMemberName("content_neighbours")] ContentNeighbours,

    /// <summary>The times of the files fit the period of the folder (dates in names, capture dates, modification times).</summary>
    [JsonStringEnumMemberName("period")] Period,

    /// <summary>Words of the names agree (folder name, file names, document kind such as invoices).</summary>
    [JsonStringEnumMemberName("name")] Name,

    /// <summary>The folder holds files of the same kind (photos, documents, music, ...).</summary>
    [JsonStringEnumMemberName("file_type")] FileType,

    /// <summary>The folder's subfolders are of the same kind as the source folder (occasions next to occasions).</summary>
    [JsonStringEnumMemberName("siblings")] Siblings,

    /// <summary>
    /// What the files show fits what the folder shows (https://github.com/crack666/smart_file_organizer/issues/56):
    /// the description of the unit against the description of a sample of the folder, judged by the local model.
    /// </summary>
    [JsonStringEnumMemberName("content_fit")] ContentFit
}

/// <summary>The judgement of the content of a unit against the content of a candidate folder.</summary>
public enum ContentFit
{
    /// <summary>The same occasion, or the same kind of document from the same issuer.</summary>
    [JsonStringEnumMemberName("fits")] Fits,

    /// <summary>Clearly something else (another occasion, another issuer), whatever the period says.</summary>
    [JsonStringEnumMemberName("contradicts")] Contradicts,

    /// <summary>The descriptions are too thin to tell.</summary>
    [JsonStringEnumMemberName("unclear")] Unclear
}

/// <summary>Whether the content of a match was checked, and why not.</summary>
public enum ContentCheckStatus
{
    /// <summary>The model compared the descriptions.</summary>
    [JsonStringEnumMemberName("checked")] Checked,

    /// <summary><c>--no-model</c>: places come from time, names and file types only.</summary>
    [JsonStringEnumMemberName("no_model")] NoModel,

    /// <summary>The local model is not reachable.</summary>
    [JsonStringEnumMemberName("unavailable")] Unavailable,

    /// <summary>The files of the unit are not described (not analysed, or nothing to describe).</summary>
    [JsonStringEnumMemberName("no_description")] NoDescription,

    /// <summary>No candidate folder to check.</summary>
    [JsonStringEnumMemberName("no_candidates")] NoCandidates,

    /// <summary>The model gave no usable answer.</summary>
    [JsonStringEnumMemberName("failed")] Failed
}

/// <summary>Where the description of a library folder comes from.</summary>
public enum FolderDescriptionSource
{
    /// <summary>A small sample of the folder's own files, described like the files of a source.</summary>
    [JsonStringEnumMemberName("sample")] Sample,

    /// <summary>Descriptions another program already holds (reserved; found by the checksum of a file).</summary>
    [JsonStringEnumMemberName("external")] External
}

/// <summary>What <c>sfo library match</c> was asked about.</summary>
public enum MatchUnitKind
{
    [JsonStringEnumMemberName("file")] File,
    [JsonStringEnumMemberName("folder")] Folder,
    [JsonStringEnumMemberName("bundle")] Bundle
}

/// <summary>How the library of a match was chosen.</summary>
public enum LibraryChoice
{
    /// <summary>Given with <c>--against</c>.</summary>
    [JsonStringEnumMemberName("option")] Option,

    /// <summary>The library of the source's latest <c>sfo compare</c>.</summary>
    [JsonStringEnumMemberName("comparison")] Comparison,

    /// <summary>The newest completed library job (there was no comparison).</summary>
    [JsonStringEnumMemberName("latest")] Latest
}

/// <summary>Whether <c>sfo library match</c> had a library to work with.</summary>
public enum MatchStatus
{
    [JsonStringEnumMemberName("ok")] Ok,

    /// <summary>No library has been scanned: nothing to match against, and nothing is proposed.</summary>
    [JsonStringEnumMemberName("no_library")] NoLibrary
}
