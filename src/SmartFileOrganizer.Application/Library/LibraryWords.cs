using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Application.Library;

/// <summary>A word file that cannot be used. <see cref="Exception.Message"/> lists every problem found.</summary>
public sealed class LibraryWordsException(string source, IReadOnlyList<string> problems)
    : Exception($"Invalid word file ({source}): {string.Join("; ", problems)}")
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// The word lists of the library review, as data: placeholder and catch-all names, words that promise a
/// file type, spellings of a level, markers of dumps, and the person's exceptions. The shipped lists are
/// <c>Library/library-words.json</c> (embedded); a file given with <c>--words</c> adds to them.
/// </summary>
public sealed class LibraryWords
{
    private const string DefaultResource = "SmartFileOrganizer.Application.Library.library-words.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Lazy<LibraryWords> DefaultWords = new(() =>
    {
        using var stream = typeof(LibraryWords).Assembly.GetManifestResourceStream(DefaultResource)
            ?? throw new InvalidOperationException($"Embedded resource {DefaultResource} is missing.");
        using var reader = new StreamReader(stream);
        return FromDto(Parse(reader.ReadToEnd(), "built-in"), "built-in");
    });

    /// <summary>The shipped lists.</summary>
    public static LibraryWords Default => DefaultWords.Value;

    public IReadOnlySet<string> SystemFiles { get; private init; } = new HashSet<string>();
    public IReadOnlyList<string> SystemFilePrefixes { get; private init; } = [];
    public IReadOnlySet<string> PlaceholderNames { get; private init; } = new HashSet<string>();
    public IReadOnlySet<string> CatchAllNames { get; private init; } = new HashSet<string>();

    /// <summary>Word → the file type the word promises.</summary>
    public IReadOnlyDictionary<string, FileType> TypeWords { get; private init; } = new Dictionary<string, FileType>();

    public IReadOnlyList<VariantGroup> VariantGroups { get; private init; } = [];
    public IReadOnlySet<string> CommonChildNames { get; private init; } = new HashSet<string>();
    public IReadOnlySet<string> StopWords { get; private init; } = new HashSet<string>();

    /// <summary>Everyday words that say little on their own; a name agrees only by other words (Issue 85).</summary>
    public IReadOnlySet<string> CommonWords { get; private init; } = new HashSet<string>();

    /// <summary>Folder names of technical parts (cache, thumbnails, css): closed for loose files (Issue 85).</summary>
    public IReadOnlySet<string> TechnicalNames { get; private init; } = new HashSet<string>();
    public IReadOnlyList<string> SensitiveNameWords { get; private init; } = [];
    public IReadOnlySet<string> DeviceMarkers { get; private init; } = new HashSet<string>();
    public IReadOnlySet<string> ComputerMarkers { get; private init; } = new HashSet<string>();
    public IReadOnlySet<string> PackageMarkers { get; private init; } = new HashSet<string>();
    public Regex AppPackagePattern { get; private init; } = new("(?!)");
    public int AppPackageMinChildren { get; private init; } = 3;
    public IReadOnlyList<LibraryException> Exceptions { get; private init; } = [];

    /// <summary>Where the lists came from: <c>built-in</c>, or <c>built-in + &lt;file&gt;</c>.</summary>
    public string Source { get; private init; } = "built-in";

    /// <summary>The shipped lists plus the entries of <paramref name="path"/>.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="LibraryWordsException">The file is not a valid word file.</exception>
    public static LibraryWords LoadWithDefaults(string path)
    {
        var extra = Parse(File.ReadAllText(path), path);
        return FromDto(Merge(Parse(ReadDefaultJson(), "built-in"), extra), $"built-in + {path}");
    }

    /// <summary>Lower case, umlauts spelled out, surrounding space removed: the form every list is compared in.</summary>
    public static string Normalize(string name)
    {
        var lower = name.Normalize(NormalizationForm.FormC).Trim().ToLower(CultureInfo.InvariantCulture);
        return lower.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
    }

    public bool IsSystemFile(string fileName)
    {
        var n = Normalize(fileName);
        return SystemFiles.Contains(n) || SystemFilePrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal));
    }

    private static string ReadDefaultJson()
    {
        using var stream = typeof(LibraryWords).Assembly.GetManifestResourceStream(DefaultResource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static WordsDto Parse(string json, string source)
    {
        WordsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<WordsDto>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new LibraryWordsException(source, [ex.Message]);
        }
        if (dto == null)
            throw new LibraryWordsException(source, ["the file is empty or 'null'"]);

        var problems = new List<string>();
        if (dto.SchemaVersion is { } v && v != 1)
            problems.Add($"schema_version {v} is not supported (expected 1)");
        foreach (var key in dto.TypeWords?.Keys ?? Enumerable.Empty<string>())
        {
            if (!TryParseFileType(key, out _))
                problems.Add($"type_words: '{key}' is not a file type");
        }
        foreach (var group in dto.VariantGroups ?? [])
        {
            if (string.IsNullOrWhiteSpace(group.Role))
                problems.Add("variant_groups: a group has no role");
            foreach (var variant in group.Variants ?? [])
            {
                if (string.IsNullOrWhiteSpace(variant.Id) || string.IsNullOrWhiteSpace(variant.Pattern))
                    problems.Add($"variant_groups '{group.Role}': a variant needs id and pattern");
                else if (!IsValidRegex(variant.Pattern))
                    problems.Add($"variant_groups '{group.Role}': pattern of '{variant.Id}' is not a valid regular expression");
            }
        }
        if (dto.AppPackagePattern is { } pattern && !IsValidRegex(pattern))
            problems.Add("app_package_pattern is not a valid regular expression");
        foreach (var exception in dto.Exceptions ?? [])
        {
            if (string.IsNullOrWhiteSpace(exception.Path))
                problems.Add("exceptions: an entry has no path");
            foreach (var kind in exception.Kinds ?? [])
            {
                if (!VerdictCodes.TryParse<LibraryFindingKind>(kind, out _))
                    problems.Add($"exceptions '{exception.Path}': '{kind}' is not a finding kind");
            }
        }
        if (problems.Count > 0)
            throw new LibraryWordsException(source, problems);
        return dto;
    }

    private static WordsDto Merge(WordsDto a, WordsDto b) => new()
    {
        SchemaVersion = 1,
        SystemFiles = [.. a.SystemFiles ?? [], .. b.SystemFiles ?? []],
        SystemFilePrefixes = [.. a.SystemFilePrefixes ?? [], .. b.SystemFilePrefixes ?? []],
        PlaceholderNames = [.. a.PlaceholderNames ?? [], .. b.PlaceholderNames ?? []],
        CatchAllNames = [.. a.CatchAllNames ?? [], .. b.CatchAllNames ?? []],
        TypeWords = (a.TypeWords ?? []).Concat(b.TypeWords ?? [])
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.SelectMany(kv => kv.Value).ToList()),
        VariantGroups = [.. a.VariantGroups ?? [], .. b.VariantGroups ?? []],
        CommonChildNames = [.. a.CommonChildNames ?? [], .. b.CommonChildNames ?? []],
        StopWords = [.. a.StopWords ?? [], .. b.StopWords ?? []],
        CommonWords = [.. a.CommonWords ?? [], .. b.CommonWords ?? []],
        TechnicalNames = [.. a.TechnicalNames ?? [], .. b.TechnicalNames ?? []],
        SensitiveNameWords = [.. a.SensitiveNameWords ?? [], .. b.SensitiveNameWords ?? []],
        DeviceMarkers = [.. a.DeviceMarkers ?? [], .. b.DeviceMarkers ?? []],
        ComputerMarkers = [.. a.ComputerMarkers ?? [], .. b.ComputerMarkers ?? []],
        PackageMarkers = [.. a.PackageMarkers ?? [], .. b.PackageMarkers ?? []],
        AppPackagePattern = b.AppPackagePattern ?? a.AppPackagePattern,
        AppPackageMinChildren = b.AppPackageMinChildren ?? a.AppPackageMinChildren,
        Exceptions = [.. a.Exceptions ?? [], .. b.Exceptions ?? []]
    };

    private static LibraryWords FromDto(WordsDto dto, string source)
    {
        static HashSet<string> Set(IEnumerable<string>? values) =>
            new((values ?? []).Select(Normalize).Where(v => v.Length > 0), StringComparer.Ordinal);

        var typeWords = new Dictionary<string, FileType>(StringComparer.Ordinal);
        foreach (var (key, words) in dto.TypeWords ?? [])
        {
            TryParseFileType(key, out var type);
            foreach (var word in words)
                typeWords[Normalize(word)] = type;
        }

        return new LibraryWords
        {
            Source = source,
            SystemFiles = Set(dto.SystemFiles),
            SystemFilePrefixes = (dto.SystemFilePrefixes ?? []).Select(Normalize).ToList(),
            PlaceholderNames = Set(dto.PlaceholderNames),
            CatchAllNames = Set(dto.CatchAllNames),
            TypeWords = typeWords,
            VariantGroups = (dto.VariantGroups ?? [])
                .Select(g => new VariantGroup(g.Role!, (g.Variants ?? [])
                    .Select(v => new Variant(v.Id!, new Regex(v.Pattern!, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                    .ToList()))
                .ToList(),
            CommonChildNames = Set(dto.CommonChildNames),
            StopWords = Set(dto.StopWords),
            CommonWords = Set(dto.CommonWords),
            TechnicalNames = Set(dto.TechnicalNames),
            SensitiveNameWords = (dto.SensitiveNameWords ?? []).Select(Normalize).Distinct().ToList(),
            DeviceMarkers = Set(dto.DeviceMarkers),
            ComputerMarkers = Set(dto.ComputerMarkers),
            PackageMarkers = Set(dto.PackageMarkers),
            AppPackagePattern = new Regex(dto.AppPackagePattern ?? "(?!)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            AppPackageMinChildren = dto.AppPackageMinChildren ?? 3,
            Exceptions = (dto.Exceptions ?? [])
                .Select(e => new LibraryException(
                    e.Path!.Replace('\\', '/').Trim('/'),
                    (e.Kinds ?? []).Select(k => { VerdictCodes.TryParse<LibraryFindingKind>(k, out var kind); return kind; }).ToList(),
                    e.Why))
                .ToList()
        };
    }

    private static bool TryParseFileType(string key, out FileType type) =>
        Enum.TryParse(key, ignoreCase: true, out type) && Enum.IsDefined(type) && !int.TryParse(key, out _);

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class WordsDto
    {
        public int? SchemaVersion { get; set; }
        public List<string>? SystemFiles { get; set; }
        public List<string>? SystemFilePrefixes { get; set; }
        public List<string>? PlaceholderNames { get; set; }
        public List<string>? CatchAllNames { get; set; }
        public Dictionary<string, List<string>>? TypeWords { get; set; }
        public List<VariantGroupDto>? VariantGroups { get; set; }
        public List<string>? CommonChildNames { get; set; }
        public List<string>? StopWords { get; set; }
        public List<string>? CommonWords { get; set; }
        public List<string>? TechnicalNames { get; set; }
        public List<string>? SensitiveNameWords { get; set; }
        public List<string>? DeviceMarkers { get; set; }
        public List<string>? ComputerMarkers { get; set; }
        public List<string>? PackageMarkers { get; set; }
        public string? AppPackagePattern { get; set; }
        public int? AppPackageMinChildren { get; set; }
        public List<ExceptionDto>? Exceptions { get; set; }
    }

    private sealed class VariantGroupDto
    {
        public string? Role { get; set; }
        public List<VariantDto>? Variants { get; set; }
    }

    private sealed class VariantDto
    {
        public string? Id { get; set; }
        public string? Pattern { get; set; }
    }

    private sealed class ExceptionDto
    {
        public string? Path { get; set; }
        public List<string>? Kinds { get; set; }
        public string? Why { get; set; }
    }
}

/// <summary>Spellings of one level, for example seasons: <c>Season 1</c>, <c>S01</c>, a release name.</summary>
public sealed record VariantGroup(string Role, IReadOnlyList<Variant> Variants)
{
    /// <summary>The first variant whose pattern matches <paramref name="name"/>, or null.</summary>
    public string? Match(string name) => Variants.FirstOrDefault(v => v.Pattern.IsMatch(name))?.Id;
}

public sealed record Variant(string Id, Regex Pattern);

/// <summary>A folder the person keeps the way it is on purpose. Empty <see cref="Kinds"/> means every kind.</summary>
public sealed record LibraryException(string Path, IReadOnlyList<LibraryFindingKind> Kinds, string? Why)
{
    /// <summary>True when a finding of <paramref name="kind"/> at <paramref name="relativePath"/> (with <c>/</c>) is covered.</summary>
    public bool Covers(LibraryFindingKind kind, string relativePath)
    {
        if (Kinds.Count > 0 && !Kinds.Contains(kind)) return false;
        var path = relativePath.Replace('\\', '/').Trim('/');
        return string.Equals(path, Path, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(Path + "/", StringComparison.OrdinalIgnoreCase)
               || Path == "." || Path.Length == 0;
    }
}
