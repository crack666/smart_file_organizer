using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Library;

/// <summary>
/// A target structure for a person without a library: a root and a few named targets, each with the kind of
/// files it holds and how its folders are named. Written by <c>sfo library propose</c>, edited by hand, read by
/// the plan (https://github.com/crack666/smart_file_organizer/issues/11) as its fallback when no library exists.
/// The format is in docs/library-review.md.
/// </summary>
/// <param name="Root">Absolute folder the targets go under; null until the person sets it.</param>
public sealed record TargetStructure(
    int SchemaVersion,
    string Kind,
    string? Root,
    long? SourceJob,
    DateTime CreatedAt,
    AdviceOrigin Origin,
    string? Model,
    IReadOnlyList<TargetEntry> Targets);

/// <summary>One target.</summary>
/// <param name="Id">Stable key (<c>photos</c>); plan actions refer to it.</param>
/// <param name="Path">Folder below the root, with <c>/</c> as separator.</param>
/// <param name="Pattern">How folders inside are named, for example <c>{occasion} {year}</c>; null for none.</param>
/// <param name="Holds">File types (codes as in <c>sfo files --type</c>) that go here.</param>
public sealed record TargetEntry(
    string Id,
    string Path,
    string? Pattern,
    IReadOnlyList<string> Holds,
    string? Reason,
    IReadOnlyList<string> Examples);

/// <summary>A target file that cannot be used. <see cref="Exception.Message"/> lists every problem.</summary>
public sealed class TargetStructureException(IReadOnlyList<string> problems)
    : Exception($"Invalid target file: {string.Join("; ", problems)}")
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>Writes, reads and validates target files.</summary>
public static partial class TargetStructureFile
{
    public const int SchemaVersion = 1;
    public const string KindValue = "sfo_target_structure";

    private static readonly JsonSerializerOptions WriteOptions = Options(write: true);
    private static readonly JsonSerializerOptions ReadOptions = Options(write: false);

    public static string Serialize(TargetStructure structure) => JsonSerializer.Serialize(structure, WriteOptions);

    /// <summary>Reads and validates a target file. Every problem is reported, not only the first.</summary>
    /// <exception cref="TargetStructureException">The JSON is not a valid target file.</exception>
    public static TargetStructure Parse(string json)
    {
        TargetStructure? structure;
        try
        {
            structure = JsonSerializer.Deserialize<TargetStructure>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new TargetStructureException([ex.Message]);
        }
        if (structure is null)
            throw new TargetStructureException(["the file is empty or 'null'"]);

        var problems = Validate(structure);
        if (problems.Count > 0)
            throw new TargetStructureException(problems);
        return structure;
    }

    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="TargetStructureException">The file is not a valid target file.</exception>
    public static TargetStructure Load(string path) => Parse(File.ReadAllText(path));

    public static IReadOnlyList<string> Validate(TargetStructure s)
    {
        var problems = new List<string>();
        if (s.SchemaVersion != SchemaVersion)
            problems.Add($"schema_version {s.SchemaVersion} is not supported (expected {SchemaVersion})");
        if (s.Kind != KindValue)
            problems.Add($"kind must be '{KindValue}'");
        if (s.Root is not null && !System.IO.Path.IsPathRooted(s.Root))
            problems.Add("root must be an absolute path or null");
        if (s.Targets is null || s.Targets.Count == 0)
        {
            problems.Add("targets must name at least one target");
            return problems;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var types = Enum.GetNames<FileType>().Select(n => n.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        foreach (var t in s.Targets)
        {
            var label = string.IsNullOrWhiteSpace(t.Id) ? "(no id)" : t.Id;
            if (string.IsNullOrWhiteSpace(t.Id) || !IdRegex().IsMatch(t.Id))
                problems.Add($"target {label}: id must be lower case letters, digits, '_' or '-'");
            else if (!ids.Add(t.Id))
                problems.Add($"target {label}: id is used twice");
            if (string.IsNullOrWhiteSpace(t.Path))
                problems.Add($"target {label}: path is missing");
            else if (System.IO.Path.IsPathRooted(t.Path) || t.Path.StartsWith('/') || t.Path.StartsWith('\\')
                     || t.Path.Replace('\\', '/').Split('/').Any(seg => seg is ".." or "." or ""))
                problems.Add($"target {label}: path must be relative to the root, without '.' or '..'");
            foreach (var h in t.Holds ?? [])
            {
                if (!types.Contains(h))
                    problems.Add($"target {label}: '{h}' in holds is not a file type");
            }
        }
        return problems;
    }

    private static JsonSerializerOptions Options(bool write)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = write,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = write ? JsonUnmappedMemberHandling.Skip : JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    [GeneratedRegex("^[a-z0-9_-]+$")]
    private static partial Regex IdRegex();
}
