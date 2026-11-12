using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Evaluation;

/// <summary>
/// The truth of an evaluation corpus as it is written to <c>truth.json</c> next to the corpus.
/// Paths are relative to <see cref="Root"/> with forward slashes; the scan root itself is <c>.</c>.
/// </summary>
/// <param name="Root">Folder next to the truth file that is scanned (default <c>tree</c>).</param>
public sealed record EvalTruthDocument(
    int SchemaVersion,
    EvalCorpusInfo Corpus,
    string Root,
    IReadOnlyList<TruthDirectoryEntry> Directories,
    IReadOnlyList<TruthFileEntry> Files);

/// <summary>Which corpus this is. Name, generator version and seed identify it for comparisons.</summary>
public sealed record EvalCorpusInfo(string Name, int GeneratorVersion, int Seed, string? Description = null);

/// <param name="Verdict">Expected verdict code, or null when the verdict of this folder is not measured.</param>
/// <param name="Note">Why this is the truth; for people reading the file.</param>
public sealed record TruthDirectoryEntry(string Path, string? Verdict, string? Note = null);

/// <param name="Category">Expected category code, or null when no single category is right (not measured).</param>
/// <param name="Sensitive">Expected marker codes; empty means the file must not be marked. Always measured.</param>
public sealed record TruthFileEntry(string Path, string? Category, IReadOnlyList<string> Sensitive, string? Note = null);

/// <summary>A directory of the truth with its parsed verdict.</summary>
public sealed record TruthDirectory(string Path, DirectoryVerdictKind? Verdict, string? Note);

/// <summary>A file of the truth with its parsed category and markers.</summary>
public sealed record TruthFile(string Path, FileCategory? Category, SensitiveMarker Sensitive, string? Note);

/// <summary>A truth file that is not unambiguous: unknown codes, duplicate or malformed paths.</summary>
public sealed class EvalTruthException(IReadOnlyList<string> problems)
    : Exception("Truth file is invalid: " + string.Join("; ", problems.Take(10)) + (problems.Count > 10 ? $" (+{problems.Count - 10} more)" : ""))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// A validated truth: every path once, every code known. Used both as the truth of a corpus and
/// as scripted answers for <see cref="ScriptedAnswerOllamaService"/> (same file shape).
/// </summary>
public sealed class EvalTruth
{
    public const int SchemaVersion = 1;
    public const string FileName = "truth.json";

    private EvalTruth(EvalCorpusInfo corpus, string root,
        IReadOnlyDictionary<string, TruthDirectory> directories, IReadOnlyDictionary<string, TruthFile> files)
    {
        Corpus = corpus;
        Root = root;
        Directories = directories;
        Files = files;
    }

    public EvalCorpusInfo Corpus { get; }
    public string Root { get; }
    public IReadOnlyDictionary<string, TruthDirectory> Directories { get; }
    public IReadOnlyDictionary<string, TruthFile> Files { get; }

    /// <summary>Identifies the corpus in stored runs, for example <c>small v1 seed 36</c>.</summary>
    public string Subject => $"{Corpus.Name} v{Corpus.GeneratorVersion} seed {Corpus.Seed}";

    public static EvalTruth Load(string path)
    {
        var json = File.ReadAllText(path);
        EvalTruthDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<EvalTruthDocument>(json, EvalJson.Options);
        }
        catch (JsonException ex)
        {
            throw new EvalTruthException([$"not valid JSON: {ex.Message}"]);
        }
        return FromDocument(doc ?? throw new EvalTruthException(["empty document"]));
    }

    public static EvalTruth FromDocument(EvalTruthDocument doc)
    {
        var problems = new List<string>();
        if (doc.SchemaVersion != SchemaVersion)
            problems.Add($"schema_version {doc.SchemaVersion} is not supported (expected {SchemaVersion})");
        if (doc.Corpus is null || string.IsNullOrWhiteSpace(doc.Corpus.Name))
            problems.Add("corpus.name is missing");
        if (string.IsNullOrWhiteSpace(doc.Root) || Path.IsPathRooted(doc.Root) || doc.Root.Contains(".."))
            problems.Add("root must be a folder name next to the truth file");

        var directories = new Dictionary<string, TruthDirectory>(StringComparer.Ordinal);
        foreach (var d in doc.Directories ?? [])
        {
            if (!TryNormalize(d.Path, problems, out var path)) continue;
            DirectoryVerdictKind? verdict = null;
            if (d.Verdict is not null)
            {
                if (EvalCodes.TryParseVerdict(d.Verdict, out var v)) verdict = v;
                else problems.Add($"directory '{path}': unknown verdict '{d.Verdict}'");
            }
            if (!directories.TryAdd(path, new TruthDirectory(path, verdict, d.Note)))
                problems.Add($"directory '{path}' is listed twice");
        }

        var files = new Dictionary<string, TruthFile>(StringComparer.Ordinal);
        foreach (var f in doc.Files ?? [])
        {
            if (!TryNormalize(f.Path, problems, out var path)) continue;
            if (path == ".")
            {
                problems.Add("a file cannot have the path '.'");
                continue;
            }
            FileCategory? category = null;
            if (f.Category is not null)
            {
                if (EvalCodes.TryParseCategory(f.Category, out var c)) category = c;
                else problems.Add($"file '{path}': unknown category '{f.Category}'");
            }
            if (f.Sensitive is null)
                problems.Add($"file '{path}': sensitive is missing (use [] for a file that must not be marked)");
            var markers = SensitiveMarker.None;
            foreach (var code in f.Sensitive ?? [])
            {
                if (EvalCodes.TryParseMarker(code, out var m)) markers |= m;
                else problems.Add($"file '{path}': unknown sensitive marker '{code}'");
            }
            if (!files.TryAdd(path, new TruthFile(path, category, markers, f.Note)))
                problems.Add($"file '{path}' is listed twice");
            if (!directories.ContainsKey(ParentOf(path)))
                problems.Add($"file '{path}': its folder '{ParentOf(path)}' is not listed");
        }

        foreach (var dir in directories.Keys.Where(p => p != "."))
        {
            if (!directories.ContainsKey(ParentOf(dir)))
                problems.Add($"directory '{dir}': its parent '{ParentOf(dir)}' is not listed");
        }
        if (directories.Count > 0 && !directories.ContainsKey("."))
            problems.Add("the scan root '.' is not listed");

        if (problems.Count > 0)
            throw new EvalTruthException(problems);
        return new EvalTruth(doc.Corpus!, doc.Root, directories, files);
    }

    /// <summary>Forward slashes, no leading or trailing slash; the root is <c>.</c>.</summary>
    public static string NormalizePath(string? path)
    {
        var p = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        return p.Length == 0 || p == "." ? "." : p;
    }

    public static string ParentOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "." : path[..i];
    }

    private static bool TryNormalize(string? raw, List<string> problems, out string path)
    {
        var normalized = NormalizePath(raw);
        path = normalized;
        if (raw is null || normalized.Split('/').Any(s => s is ".." || (s == "." && normalized != ".")) || Path.IsPathRooted(raw))
        {
            problems.Add($"path '{raw}' must be relative to the root, without '..'");
            return false;
        }
        return true;
    }
}

/// <summary>JSON shape of truth files, answer files and stored reports: snake_case, enums as codes.</summary>
public static class EvalJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: true);
    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = indented,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
