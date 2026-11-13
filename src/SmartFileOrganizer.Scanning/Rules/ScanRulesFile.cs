using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Scanning.Rules;

/// <summary>A rule file that cannot be used. <see cref="Exception.Message"/> lists every problem found.</summary>
public sealed class ScanRulesException(string source, IReadOnlyList<string> problems)
    : Exception($"Invalid scan rules ({source}): {string.Join("; ", problems)}")
{
    public string RulesSource { get; } = source;
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Reads, validates and writes scan rule files (JSON). The format is described in docs/scan-rules.md;
/// the rules shipped with the program are <c>Rules/default-rules.json</c>, embedded in this assembly.
/// </summary>
public static class ScanRulesFile
{
    /// <summary><see cref="ScanRulesProfile.Source"/> of the shipped rules.</summary>
    public const string BuiltInSource = "built-in";

    private const string DefaultResource = "SmartFileOrganizer.Scanning.Rules.default-rules.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions CompactOptions = WriteOptions(indented: false);
    private static readonly JsonSerializerOptions IndentedOptions = WriteOptions(indented: true);

    private static readonly Lazy<ScanRuleSet> DefaultRules = new(LoadDefault);

    /// <summary>The rules shipped with the program. Used for every job that names no rule file.</summary>
    public static ScanRuleSet Default => DefaultRules.Value;

    /// <summary>The shipped rules as stored at a job.</summary>
    public static ScanRulesProfile DefaultProfile => ToProfile(Default, BuiltInSource);

    /// <summary>Reads and validates a rule file. A file without <c>name</c> is named after the file.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="ScanRulesException">The file is not a valid rule file.</exception>
    public static ScanRuleSet Load(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json, Path.GetFileNameWithoutExtension(path), path);
    }

    /// <summary>Validates rules given as JSON. Every problem is reported, not only the first.</summary>
    /// <param name="fallbackName">Name to use when the JSON has no <c>name</c>.</param>
    /// <param name="source">Where the JSON came from, for error messages.</param>
    /// <exception cref="ScanRulesException">The JSON is not a valid rule file.</exception>
    public static ScanRuleSet Parse(string json, string fallbackName, string source)
    {
        FileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<FileDto>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new ScanRulesException(source, [Describe(ex)]);
        }
        if (dto == null)
            throw new ScanRulesException(source, ["the file is empty or 'null'"]);

        var problems = new List<string>();

        if (dto.SchemaVersion is { } version && version != ScanRuleSet.CurrentSchemaVersion)
            problems.Add($"schema_version {version} is not supported (expected {ScanRuleSet.CurrentSchemaVersion})");
        if (dto.ShallowFrom is < 1)
            problems.Add("shallow_from must be at least 1");
        if (dto.SafetyLimit is < 1)
            problems.Add("safety_limit must be at least 1");

        var userTypes = ParseTypes(dto.UserTypes, "user_types", problems);
        var programTypes = ParseTypes(dto.ProgramTypes, "program_types", problems);

        var rules = new List<ScanRule>();
        for (var i = 0; i < (dto.Rules?.Count ?? 0); i++)
        {
            var rule = ParseRule(dto.Rules![i], $"rules[{i}]", problems);
            if (rule != null)
                rules.Add(rule);
        }

        if (problems.Count > 0)
            throw new ScanRulesException(source, problems);

        var defaults = new ScanRuleSet { Name = fallbackName };
        return new ScanRuleSet
        {
            Name = string.IsNullOrWhiteSpace(dto.Name) ? fallbackName : dto.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim(),
            ShallowFrom = dto.ShallowFrom ?? defaults.ShallowFrom,
            SafetyLimit = dto.SafetyLimit ?? defaults.SafetyLimit,
            SkipHidden = dto.SkipHidden ?? defaults.SkipHidden,
            UserTypes = userTypes ?? defaults.UserTypes,
            ProgramTypes = programTypes ?? defaults.ProgramTypes,
            Rules = rules
        };
    }

    /// <summary>
    /// Writes the rules with every setting spelled out. Indented for people (<c>sfo rules show</c>),
    /// compact for storage at the job and for <see cref="Hash"/>.
    /// </summary>
    public static string Serialize(ScanRuleSet rules, bool indented = true) =>
        JsonSerializer.Serialize(ToDto(rules), indented ? IndentedOptions : CompactOptions);

    /// <summary>SHA-256 of the compact form: equal for equal rules, whatever the layout of the file.</summary>
    public static string Hash(ScanRuleSet rules) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(rules, indented: false))));

    public static ScanRulesProfile ToProfile(ScanRuleSet rules, string source) =>
        new(rules.Name, source, Hash(rules), Serialize(rules, indented: false));

    /// <summary>The rules stored at a job.</summary>
    /// <exception cref="ScanRulesException">The stored rules cannot be read (database edited by hand).</exception>
    public static ScanRuleSet FromProfile(ScanRulesProfile profile) =>
        Parse(profile.Json, profile.Name, profile.Source);

    /// <summary>File-format name of a file type, for example <c>image</c>.</summary>
    public static string TypeName(FileType type) => JsonNamingPolicy.SnakeCaseLower.ConvertName(type.ToString());

    /// <summary>File-format name of an action, for example <c>user_data</c>.</summary>
    public static string ActionName(ScanRuleAction action) => JsonNamingPolicy.SnakeCaseLower.ConvertName(action.ToString());

    private static ScanRule? ParseRule(RuleDto? dto, string at, List<string> problems)
    {
        if (dto == null)
        {
            problems.Add($"{at} is null");
            return null;
        }

        var ok = true;
        if (string.IsNullOrWhiteSpace(dto.Match) || PathSegments.Split(dto.Match).Length == 0)
        {
            problems.Add($"{at}.match is missing or has no folder name");
            ok = false;
        }

        ScanRuleAction? action = dto.Action switch
        {
            "skip" => ScanRuleAction.Skip,
            "shallow" => ScanRuleAction.Shallow,
            "user_data" => ScanRuleAction.UserData,
            "follow_link" => ScanRuleAction.FollowLink,
            _ => null
        };
        if (action == null)
        {
            problems.Add($"{at}.action must be 'skip', 'shallow', 'user_data' or 'follow_link'" +
                         (dto.Action == null ? " (missing)" : $", not '{dto.Action}'"));
            ok = false;
        }

        if (dto.MaxDepth != null && action is not ScanRuleAction.Shallow && action != null)
        {
            problems.Add($"{at}.max_depth is only allowed with action 'shallow'");
            ok = false;
        }
        if (dto.MaxDepth is < 0)
        {
            problems.Add($"{at}.max_depth must not be negative");
            ok = false;
        }

        if (!ok)
            return null;

        var match = string.Join('/', PathSegments.Split(dto.Match!.Trim()));
        var why = string.IsNullOrWhiteSpace(dto.Why) ? null : dto.Why.Trim();
        return new ScanRule(match, action!.Value, dto.MaxDepth ?? 0, why);
    }

    private static List<FileType>? ParseTypes(List<string>? names, string at, List<string> problems)
    {
        if (names == null)
            return null;

        var types = new List<FileType>();
        foreach (var name in names)
        {
            var type = Enum.GetValues<FileType>().FirstOrDefault(t => TypeName(t) == name, (FileType)(-1));
            if ((int)type == -1)
            {
                var valid = string.Join(", ", Enum.GetValues<FileType>().Select(TypeName));
                problems.Add($"{at} contains '{name}', which is not a file type ({valid})");
                continue;
            }
            if (!types.Contains(type))
                types.Add(type);
        }
        return types;
    }

    private static FileDto ToDto(ScanRuleSet rules) => new()
    {
        SchemaVersion = ScanRuleSet.CurrentSchemaVersion,
        Name = rules.Name,
        Description = rules.Description,
        ShallowFrom = rules.ShallowFrom,
        SafetyLimit = rules.SafetyLimit,
        SkipHidden = rules.SkipHidden,
        UserTypes = rules.UserTypes.Select(TypeName).ToList(),
        ProgramTypes = rules.ProgramTypes.Select(TypeName).ToList(),
        Rules = rules.Rules.Select(r => (RuleDto?)new RuleDto
        {
            Match = r.Match,
            Action = ActionName(r.Action),
            MaxDepth = r.Action == ScanRuleAction.Shallow ? r.MaxDepth : null,
            Why = r.Why
        }).ToList()
    };

    private static string Describe(JsonException ex)
    {
        var where = ex.Path is { Length: > 0 } path ? $" at {path}" : string.Empty;
        var line = ex.LineNumber is { } l ? $" (line {l + 1})" : string.Empty;
        // The message of an unknown property already names it; keep the first sentence only.
        var message = ex.Message.Split(". Path:", 2)[0].TrimEnd('.');
        return $"{message}{where}{line}";
    }

    private static ScanRuleSet LoadDefault()
    {
        using var stream = typeof(ScanRulesFile).Assembly.GetManifestResourceStream(DefaultResource)
            ?? throw new InvalidOperationException($"Embedded resource {DefaultResource} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), "default", BuiltInSource);
    }

    private static JsonSerializerOptions WriteOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = indented,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed class FileDto
    {
        public int? SchemaVersion { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public int? ShallowFrom { get; set; }
        public int? SafetyLimit { get; set; }
        public bool? SkipHidden { get; set; }
        public List<string>? UserTypes { get; set; }
        public List<string>? ProgramTypes { get; set; }
        public List<RuleDto?>? Rules { get; set; }
    }

    private sealed class RuleDto
    {
        public string? Match { get; set; }
        public string? Action { get; set; }
        public int? MaxDepth { get; set; }
        public string? Why { get; set; }
    }
}
