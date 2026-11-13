using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Bundles;

/// <summary>A pattern file that cannot be used; every problem of the file is in <see cref="Exception.Message"/>.</summary>
public sealed class BundlePatternsException(string message) : Exception(message);

/// <summary>
/// The data that recognises dumps, their worth-keeping parts and folder names without meaning
/// (https://github.com/crack666/smart_file_organizer/issues/39). Shipped as the embedded
/// <c>bundle-patterns.json</c>; a copy given in <see cref="BundleOptions.PatternsFile"/> replaces it.
/// </summary>
public sealed class BundlePatterns
{
    private const string DefaultResource = "SmartFileOrganizer.Application.Bundles.bundle-patterns.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly Lazy<BundlePatterns> LazyDefault = new(() =>
    {
        using var stream = typeof(BundlePatterns).Assembly.GetManifestResourceStream(DefaultResource)
                           ?? throw new InvalidOperationException($"Embedded resource {DefaultResource} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd(), "built-in");
    });

    private BundlePatterns(PatternFile file, string source, string hash)
    {
        Name = file.Name ?? "default";
        Source = source;
        Hash = hash;
        Signatures = file.Signatures!.Select(s => new DumpSignature(s)).ToList();
        KeepParts = file.KeepParts!
            .SelectMany(p => p.Paths!.Select(path => (Segments: SplitPath(path), Part: ParsePart(p.Part!))))
            .ToList();
        NoisePaths = (file.NoisePaths ?? []).Select(SplitPath).ToList();
        SystemPlaces = (file.SystemPlaces ?? [])
            .SelectMany(p => p.Paths!.Select(path => (SplitPath(path), p.Rule!.Trim(), path)))
            .ToList();
        GenericNames = new HashSet<string>((file.GenericFolderNames ?? []).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        GenericRegexes = (file.GenericFolderRegexes ?? []).Select(r => new Regex(r, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
    }

    public static BundlePatterns Default => LazyDefault.Value;

    public string Name { get; }

    /// <summary><c>built-in</c> or the full path of the file.</summary>
    public string Source { get; }

    /// <summary>SHA-256 of the file text, first 12 hex digits.</summary>
    public string Hash { get; }

    public IReadOnlyList<DumpSignature> Signatures { get; }
    public IReadOnlyList<(string[] Segments, DumpPart Part)> KeepParts { get; }
    public IReadOnlyList<string[]> NoisePaths { get; }

    /// <summary>Entries of <c>system_places</c>: the path split into folder names, the rule name, the path as written.</summary>
    public IReadOnlyList<(string[] Run, string Rule, string Path)> SystemPlaces { get; }
    public IReadOnlySet<string> GenericNames { get; }
    public IReadOnlyList<Regex> GenericRegexes { get; }

    /// <summary>Loads a pattern file; throws <see cref="BundlePatternsException"/> listing every problem.</summary>
    public static BundlePatterns Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new BundlePatternsException($"Bundle pattern file not found: {full}");
        return Parse(File.ReadAllText(full), full);
    }

    public static BundlePatterns Parse(string json, string source)
    {
        PatternFile? file;
        try
        {
            file = JsonSerializer.Deserialize<PatternFile>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new BundlePatternsException($"{source}: {ex.Message}");
        }

        var problems = Validate(file);
        if (problems.Count > 0)
            throw new BundlePatternsException($"{source}: " + string.Join(" ", problems));

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..12];
        return new BundlePatterns(file!, source, hash);
    }

    /// <summary>
    /// The one place that decides whether a directory is a dump: the best-scoring signature whose
    /// threshold and key marker are met, or <c>null</c>. Used by the bundles and by the library review.
    /// </summary>
    /// <param name="subdirectories">Names of the direct subdirectories.</param>
    /// <param name="files">Names of the direct files.</param>
    /// <param name="isRoot">The scan root itself; only signatures with <c>at_root</c> apply there.</param>
    /// <param name="mediaShare">Share of photos and videos among the files of the subtree, asked only when a signature limits it.</param>
    public DumpRecognition? Recognise(
        IReadOnlyCollection<string> subdirectories, IReadOnlyCollection<string> files, bool isRoot, Func<double> mediaShare)
    {
        DumpRecognition? best = null;
        foreach (var signature in Signatures)
        {
            if (isRoot && !signature.AtRoot) continue;
            var (score, hasKey, matched) = signature.Evaluate(subdirectories, files);
            if (score < signature.MinScore || !hasKey) continue;
            if (signature.MaxMediaShare is { } maxShare && mediaShare() > maxShare) continue;
            if (best is null || score > best.Score)
                best = new DumpRecognition(signature, score, matched);
        }
        return best;
    }

    /// <summary>
    /// The part of a directory inside a dump, by its path below the dump root (<paramref name="segments"/>, empty
    /// for the root itself): noise (null) below a noise path; otherwise the keep path that ends deepest (the
    /// longest on a tie); otherwise noise. The bundles split a dump by this; the directory verdict asks
    /// <see cref="NoisePathOf"/>, which is the first of these steps alone.
    /// </summary>
    public DumpPart? PartOf(IReadOnlyList<string> segments)
    {
        if (NoisePathOf(segments) is not null)
            return null;

        for (var end = segments.Count; end >= 1; end--)
        {
            DumpPart? best = null;
            var bestLength = 0;
            foreach (var (path, part) in KeepParts)
            {
                if (path.Length > end || path.Length <= bestLength) continue;
                if (RunEndsAt(segments, path, end))
                {
                    best = part;
                    bestLength = path.Length;
                }
            }
            if (best is not null)
                return best;
        }
        return null;
    }

    /// <summary>
    /// The entry of <c>noise_paths</c> a directory lies at or below (path below the dump root, either separator),
    /// joined with <c>/</c>; null when it lies below none. Such a directory is app data, cache or system whatever
    /// deeper names say.
    /// </summary>
    public string? NoisePathOf(IReadOnlyList<string> segments)
    {
        foreach (var noise in NoisePaths)
        {
            for (var end = noise.Length; end <= segments.Count; end++)
            {
                if (RunEndsAt(segments, noise, end))
                    return string.Join('/', noise);
            }
        }
        return null;
    }

    /// <summary>
    /// The <c>system_places</c> entry a directory lies at or below, by the folder names of its full path; null when
    /// none. A path is a run of folder names that may start anywhere in the path; <c>*</c> stands for one folder of
    /// any name, and <c>a|b</c> for one folder named a or b.
    /// </summary>
    public SystemPlace? SystemPlaceOf(IReadOnlyList<string> segments)
    {
        foreach (var (run, rule, path) in SystemPlaces)
        {
            for (var end = run.Length; end <= segments.Count; end++)
            {
                if (RunEndsAt(segments, run, end))
                    return new SystemPlace(rule, path);
            }
        }
        return null;
    }

    private static bool RunEndsAt(IReadOnlyList<string> segments, string[] run, int end)
    {
        var start = end - run.Length;
        for (var i = 0; i < run.Length; i++)
        {
            if (run[i] == "*") continue;
            var segment = segments[start + i];
            if (!run[i].Split('|').Any(name => string.Equals(segment, name, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return true;
    }

    /// <summary>A folder name that says nothing about an occasion (<c>Neuer Ordner</c>, <c>100CANON</c>, <c>2015</c>).</summary>
    public bool IsGenericName(string name)
    {
        // "Neu (2)" and "Bilder - Kopie" are as generic as "Neu" and "Bilder".
        var trimmed = CopySuffix.Replace(name.Trim(), string.Empty).Trim();
        if (GenericNames.Contains(trimmed)) return true;
        if (GenericRegexes.Any(r => r.IsMatch(trimmed))) return true;
        // Fewer than three letters cannot name an occasion: "x", "01", "A 2".
        return trimmed.Count(char.IsLetter) < 3;
    }

    private static readonly Regex CopySuffix = new(
        @"(\s*-\s*(Kopie|Copy))?(\s*\(\d+\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string[] SplitPath(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static DumpPart ParsePart(string code) =>
        VerdictCodes.TryParse<DumpPart>(code, out var part) ? part : throw new BundlePatternsException($"Unknown part '{code}'.");

    private static List<string> Validate(PatternFile? file)
    {
        var problems = new List<string>();
        if (file is null)
        {
            problems.Add("The file is empty.");
            return problems;
        }

        if (file.SchemaVersion != 1)
            problems.Add($"schema_version must be 1, not {file.SchemaVersion}.");
        if (file.Signatures is null || file.Signatures.Count == 0)
            problems.Add("signatures must list at least one signature.");
        if (file.KeepParts is null)
            problems.Add("keep_parts is missing.");

        for (var i = 0; i < (file.Signatures?.Count ?? 0); i++)
        {
            var s = file.Signatures![i];
            var at = $"signatures[{i}]";
            if (!VerdictCodes.TryParse<DumpOrigin>(s.Origin, out _))
                problems.Add($"{at}.origin '{s.Origin}' is not one of {string.Join(", ", VerdictCodes.All<DumpOrigin>())}.");
            if (s.MinScore < 1)
                problems.Add($"{at}.min_score must be at least 1.");
            if (s.MaxMediaShare is < 0 or > 1)
                problems.Add($"{at}.max_media_share must be between 0 and 1.");
            if (s.Markers is null || s.Markers.Count == 0)
                problems.Add($"{at}.markers must list at least one marker.");
            else if (!s.Markers.Any(m => m.Key))
                problems.Add($"{at} needs at least one marker with \"key\": true.");

            for (var j = 0; j < (s.Markers?.Count ?? 0); j++)
            {
                var m = s.Markers![j];
                var mat = $"{at}.markers[{j}]";
                var set = new[] { m.Dir, m.DirRegex, m.File, m.FileRegex }.Count(v => v is not null);
                if (set != 1)
                    problems.Add($"{mat} must have exactly one of dir, dir_regex, file, file_regex.");
                if (m.Weight < 1)
                    problems.Add($"{mat}.weight must be at least 1.");
                if (m.MaxCount < 1)
                    problems.Add($"{mat}.max_count must be at least 1.");
                foreach (var regex in new[] { m.DirRegex, m.FileRegex }.Where(r => r is not null))
                {
                    try { _ = new Regex(regex!); }
                    catch (ArgumentException ex) { problems.Add($"{mat}: invalid regex: {ex.Message}"); }
                }
            }
        }

        for (var i = 0; i < (file.KeepParts?.Count ?? 0); i++)
        {
            var p = file.KeepParts![i];
            if (!VerdictCodes.TryParse<DumpPart>(p.Part, out _))
                problems.Add($"keep_parts[{i}].part '{p.Part}' is not one of {string.Join(", ", VerdictCodes.All<DumpPart>())}.");
            if (p.Paths is null || p.Paths.Count == 0 || p.Paths.Any(path => SplitPath(path).Length == 0))
                problems.Add($"keep_parts[{i}].paths must list at least one non-empty path.");
        }

        for (var i = 0; i < (file.SystemPlaces?.Count ?? 0); i++)
        {
            var p = file.SystemPlaces![i];
            if (string.IsNullOrWhiteSpace(p.Rule))
                problems.Add($"system_places[{i}].rule is missing.");
            if (p.Paths is null || p.Paths.Count == 0 || p.Paths.Any(path => SplitPath(path).Length == 0))
                problems.Add($"system_places[{i}].paths must list at least one non-empty path.");
            else if (p.Paths.Any(path => SplitPath(path).All(s => s == "*")))
                problems.Add($"system_places[{i}].paths must name at least one folder, not only '*'.");
        }

        for (var i = 0; i < (file.GenericFolderRegexes?.Count ?? 0); i++)
        {
            try { _ = new Regex(file.GenericFolderRegexes![i]); }
            catch (ArgumentException ex) { problems.Add($"generic_folder_regexes[{i}]: invalid regex: {ex.Message}"); }
        }

        return problems;
    }

    // ---------------------------------------------------------------- file shape

    private sealed class PatternFile
    {
        public int SchemaVersion { get; set; }
        public string? Name { get; set; }
        public List<SignatureEntry>? Signatures { get; set; }
        public List<KeepPartEntry>? KeepParts { get; set; }
        public List<string>? NoisePaths { get; set; }
        public List<SystemPlaceEntry>? SystemPlaces { get; set; }
        public List<string>? GenericFolderNames { get; set; }
        public List<string>? GenericFolderRegexes { get; set; }
    }

    internal sealed class SignatureEntry
    {
        public string? Name { get; set; }
        public string? Origin { get; set; }
        public bool Split { get; set; }
        public bool AtRoot { get; set; }
        public int MinScore { get; set; } = 1;
        public double? MaxMediaShare { get; set; }
        public List<MarkerEntry>? Markers { get; set; }
    }

    internal sealed class MarkerEntry
    {
        public string? Dir { get; set; }
        public string? DirRegex { get; set; }
        public string? File { get; set; }
        public string? FileRegex { get; set; }
        public string? Label { get; set; }
        public int Weight { get; set; } = 1;
        public int MaxCount { get; set; } = 1;
        public bool Key { get; set; }
    }

    private sealed class KeepPartEntry
    {
        public string? Part { get; set; }
        public List<string>? Paths { get; set; }
    }

    private sealed class SystemPlaceEntry
    {
        public string? Rule { get; set; }
        public List<string>? Paths { get; set; }
    }
}

/// <summary>
/// A directory at or below a <c>system_places</c> path: content of programs or of the operating system in a place
/// a person also uses (learning material of an application in the public documents, account pictures, shortcuts
/// on the public desktop, package manager folders). <see cref="Rule"/> is the name of the entry, <see cref="Path"/>
/// the path that matched, as written in the file.
/// </summary>
public sealed record SystemPlace(string Rule, string Path);

/// <summary>One way of recognising a dump from the names directly inside a directory.</summary>
/// <summary>A directory recognised as a dump by <see cref="BundlePatterns.Recognise"/>.</summary>
public sealed record DumpRecognition(DumpSignature Signature, int Score, List<string> Markers)
{
    public string Describe() =>
        $"{Signature.Origin.ToCode()} dump by signature '{Signature.Name}' "
        + $"(score {Score} of at least {Signature.MinScore}): {string.Join(", ", Markers)}";
}

public sealed class DumpSignature
{
    internal DumpSignature(BundlePatterns.SignatureEntry entry)
    {
        Name = entry.Name ?? entry.Origin!;
        Origin = VerdictCodes.TryParse<DumpOrigin>(entry.Origin, out var origin) ? origin : default;
        Split = entry.Split;
        AtRoot = entry.AtRoot;
        MinScore = entry.MinScore;
        MaxMediaShare = entry.MaxMediaShare;
        Markers = entry.Markers!.Select(m => new DumpMarker(m)).ToList();
    }

    public string Name { get; }
    public DumpOrigin Origin { get; }

    /// <summary>True: the dump is split into worth-keeping parts and noise. False: it stays one bundle.</summary>
    public bool Split { get; }

    /// <summary>Whether the scan root itself may be this kind of dump.</summary>
    public bool AtRoot { get; }

    public int MinScore { get; }
    public double? MaxMediaShare { get; }
    public IReadOnlyList<DumpMarker> Markers { get; }

    /// <summary>
    /// Score of a directory from the names of its subdirectories and files; the matched markers are
    /// returned as <c>dir:DCIM</c>, <c>file:NTUSER.DAT</c> or <c>dir~package:com.example.app</c>.
    /// </summary>
    public (int Score, bool HasKey, List<string> Matched) Evaluate(IReadOnlyCollection<string> subdirectories, IReadOnlyCollection<string> files)
    {
        var score = 0;
        var hasKey = false;
        var matched = new List<string>();
        foreach (var marker in Markers)
        {
            var hits = marker.Matches(marker.OnDirectories ? subdirectories : files);
            if (hits.Count == 0) continue;
            score += marker.Weight * Math.Min(hits.Count, marker.MaxCount);
            hasKey |= marker.Key;
            matched.AddRange(hits.Take(marker.MaxCount).Select(marker.Describe));
        }
        return (score, hasKey, matched);
    }
}

public sealed class DumpMarker
{
    private readonly string? _name;
    private readonly Regex? _regex;
    private readonly string? _label;

    internal DumpMarker(BundlePatterns.MarkerEntry entry)
    {
        OnDirectories = entry.Dir is not null || entry.DirRegex is not null;
        _name = entry.Dir ?? entry.File;
        var pattern = entry.DirRegex ?? entry.FileRegex;
        _regex = pattern is null ? null : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        _label = entry.Label;
        Weight = entry.Weight;
        MaxCount = entry.MaxCount;
        Key = entry.Key;
    }

    public bool OnDirectories { get; }
    public int Weight { get; }
    public int MaxCount { get; }
    public bool Key { get; }

    public List<string> Matches(IReadOnlyCollection<string> names) => _regex is null
        ? names.Where(n => string.Equals(n, _name, StringComparison.OrdinalIgnoreCase)).Take(1).ToList()
        : names.Where(n => _regex.IsMatch(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();

    public string Describe(string hit)
    {
        var kind = OnDirectories ? "dir" : "file";
        return _regex is null ? $"{kind}:{_name}" : $"{kind}~{_label ?? "regex"}:{hit}";
    }
}
