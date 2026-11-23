using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using SmartFileOrganizer.Cli.Commands;

namespace SmartFileOrganizer.Cli.Contract;

/// <summary>
/// JSON schemas of the <c>result</c> part of the read commands <c>files</c>, <c>dirs</c>, <c>show</c> and <c>summary</c>
/// (https://github.com/crack666/smart_file_organizer/issues/116), published under <c>schemas/cli/v1/</c>.
/// </summary>
/// <remarks>
/// The shape comes from the result records themselves (<see cref="JsonSchemaExporter"/> with the options of
/// <see cref="CliJson"/>), so a new field reaches the schema with the code; every object lists all its keys as required
/// and allows no others, as the commands write every key. Fields that carry a code point into the exchange schemas
/// (<c>schemas/exchange/v1/</c>), so a category, file type, status or verdict in the CLI and in an export are the same
/// codes by construction. A test keeps the files equal to this generator and checks real outputs against them; after
/// a deliberate change write them again with <c>SFO_WRITE_SCHEMAS=1 dotnet test --filter CliSchemaTests</c>.
/// </remarks>
public static class CliSchemas
{
    /// <summary>Folder of the schema files below the repository root.</summary>
    public static readonly string[] Folder = ["schemas", "cli", $"v{CliJson.SchemaVersion}"];

    /// <summary>The described commands: name, schema file, type of their <c>result</c>.</summary>
    public static IReadOnlyList<(string Command, string File, Type Result)> Commands { get; } =
    [
        ("files", "files.result.schema.json", typeof(PageView<FileListView>)),
        ("dirs", "dirs.result.schema.json", typeof(PageView<DirectoryView>)),
        ("show", "show.result.schema.json", typeof(ShowCommand.ShowResult)),
        ("summary", "summary.result.schema.json", typeof(SummaryCommand.SummaryResult))
    ];

    private const string Base = "https://github.com/crack666/smart_file_organizer/schemas/";
    private const string FileDefs = Base + "exchange/v1/file.schema.json#/$defs/";
    private const string DirectoryDefs = Base + "exchange/v1/directory.schema.json#/$defs/";
    private const string FindingDefs = Base + "exchange/v1/finding.schema.json#/$defs/";

    /// <summary>A field holding a code: the exchange definition and the codes the CLI adds to it (<c>none</c>, ...).</summary>
    private sealed record Code(string Ref, params string[] Extra);

    /// <summary>Why a summary or dirs line has no verdict: <c>none</c> (not judged), <c>error</c> (Phase 1 failed).</summary>
    private static readonly string[] VerdictExtra = ["none", "error"];

    // String fields and lists of strings: (declaring type, JSON name) -> code.
    private static readonly Dictionary<(Type, string), Code> Fields = new()
    {
        [(typeof(FileListView), "file_type")] = new(FileDefs + "FileType"),
        [(typeof(FileListView), "status")] = new(FileDefs + "FileNodeStatus"),
        [(typeof(FileListView), "category")] = new(FileDefs + "FileCategory"),
        [(typeof(FileListView), "category_source")] = new(FileDefs + "ResultOrigin"),
        [(typeof(FileListView), "certainty")] = new(FileDefs + "ResultCertainty"),
        [(typeof(FileListView), "sensitive")] = new(FileDefs + "SensitiveMarker"),
        [(typeof(FileListView), "dir_verdict")] = new(DirectoryDefs + "DirectoryVerdictKind", "error"),
        [(typeof(ShowCommand.FileDetailView), "file_type")] = new(FileDefs + "FileType"),
        [(typeof(ShowCommand.FileDetailView), "status")] = new(FileDefs + "FileNodeStatus"),
        [(typeof(ShowCommand.FileDetailView), "category")] = new(FileDefs + "FileCategory"),
        [(typeof(ShowCommand.FileDetailView), "category_source")] = new(FileDefs + "ResultOrigin"),
        [(typeof(ShowCommand.FileDetailView), "importance")] = new(FileDefs + "Importance"),
        [(typeof(ShowCommand.FileDetailView), "certainty")] = new(FileDefs + "ResultCertainty"),
        [(typeof(ShowCommand.FileDetailView), "sensitive")] = new(FileDefs + "SensitiveMarker"),
        [(typeof(ShowCommand.FileDetailView), "dir_verdict")] = new(DirectoryDefs + "DirectoryVerdictKind", "error"),
        [(typeof(ShowCommand.FileDetailView), "dir_retention")] = new(DirectoryDefs + "RetentionValue"),
        [(typeof(Application.Services.FactItem), "fact")] = new(FileDefs + "FileFactKey"),
        [(typeof(Application.Services.FactItem), "source")] = new(FileDefs + "FactSource"),
        [(typeof(Application.Services.FactItem), "certainty")] = new(FileDefs + "ResultCertainty"),
        [(typeof(DirectoryView), "verdict")] = new(DirectoryDefs + "DirectoryVerdictKind", "error"),
        [(typeof(DirectoryView), "verdict_source")] = new(DirectoryDefs + "VerdictSource"),
        [(typeof(DirectoryView), "retention")] = new(DirectoryDefs + "RetentionValue"),
        [(typeof(DirectoryView), "sensitive")] = new(DirectoryDefs + "SensitiveMarker"),
        [(typeof(DirectoryView), "link_kind")] = new(DirectoryDefs + "LinkKind"),
        [(typeof(ShowCommand.DirectoryDetailView), "verdict")] = new(DirectoryDefs + "DirectoryVerdictKind"),
        [(typeof(ShowCommand.DirectoryDetailView), "verdict_source")] = new(DirectoryDefs + "VerdictSource"),
        [(typeof(ShowCommand.DirectoryDetailView), "retention")] = new(DirectoryDefs + "RetentionValue"),
        [(typeof(ShowCommand.DirectoryDetailView), "sensitive")] = new(DirectoryDefs + "SensitiveMarker"),
        [(typeof(SummaryCommand.LargeDirectoryView), "verdict")] = new(DirectoryDefs + "DirectoryVerdictKind", VerdictExtra),
        [(typeof(SummaryCommand.LargeDirectoryView), "retention")] = new(DirectoryDefs + "RetentionValue"),
    };

    // Count dictionaries: (declaring type, JSON name) -> code of the keys.
    private static readonly Dictionary<(Type, string), Code> Keys = new()
    {
        [(typeof(SummaryCommand.SummaryResult), "by_category")] = new(FileDefs + "FileCategory", "unclassified"),
        [(typeof(SummaryCommand.SummaryResult), "by_file_type")] = new(FileDefs + "FileType"),
        [(typeof(SummaryCommand.SummaryResult), "by_status")] = new(FileDefs + "FileNodeStatus"),
        [(typeof(SummaryCommand.SummaryResult), "by_sensitive_marker")] = new(FileDefs + "SensitiveMarker"),
        [(typeof(SummaryCommand.SummaryResult), "directories_by_verdict")] = new(DirectoryDefs + "DirectoryVerdictKind", VerdictExtra),
        [(typeof(SummaryCommand.SummaryResult), "directories_by_retention")] = new(DirectoryDefs + "RetentionValue", "none"),
        [(typeof(SummaryCommand.FindingCountsView), "by_kind")] = new(FindingDefs + "FindingKind"),
        [(typeof(SummaryCommand.FindingCountsView), "by_certainty")] = new(FindingDefs + "ResultCertainty"),
        [(typeof(SummaryCommand.FindingCountsView), "by_state")] = new(FindingDefs + "FindingState"),
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The schema text of one command, as the file under <see cref="Folder"/> holds it.</summary>
    public static string Generate(string command)
    {
        var (name, file, type) = Commands.Single(c => c.Command == command);
        var body = CliJson.Document.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform
        }).AsObject();

        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = $"{Base}cli/v{CliJson.SchemaVersion}/{file}",
            ["title"] = $"sfo {name} --json: result",
            ["description"] = $"The 'result' of 'sfo {name} --json' (envelope schema_version {CliJson.SchemaVersion}). Generated from the result records; code fields refer to the exchange schemas. docs/cli.md describes every field."
        };
        foreach (var (key, value) in body.ToList())
        {
            body.Remove(key);
            schema[key] = value;
        }
        return schema.ToJsonString(WriteOptions).ReplaceLineEndings("\n") + "\n";
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
    {
        var type = context.TypeInfo.Type;
        if (type == typeof(DateTime) || type == typeof(DateTime?))
        {
            // CliJson writes every time with its own converter, which the exporter cannot see through.
            var nullable = type == typeof(DateTime?) || IsNullable(node);
            node = new JsonObject
            {
                ["type"] = nullable ? new JsonArray("string", "null") : "string",
                ["format"] = "date-time"
            };
        }

        if (node is JsonObject obj && obj["properties"] is JsonObject properties)
        {
            obj["required"] = new JsonArray(properties.Select(p => (JsonNode)p.Key).ToArray());
            obj["additionalProperties"] = false;
        }

        if (context.PropertyInfo is { } property)
        {
            var key = (property.DeclaringType, property.Name);
            if (Fields.TryGetValue(key, out var code))
                node = WithCode(node, code);
            else if (Keys.TryGetValue(key, out var keys) && node is JsonObject dict)
                dict["propertyNames"] = CodeNode(keys);
        }
        return node;
    }

    private static bool IsNullable(JsonNode node) =>
        node is JsonObject { } o && o["type"] is JsonArray types && types.Any(t => t?.GetValue<string>() == "null");

    private static JsonNode CodeNode(Code code)
    {
        var reference = new JsonObject { ["$ref"] = code.Ref };
        return code.Extra.Length == 0
            ? reference
            : new JsonObject { ["anyOf"] = new JsonArray(reference, new JsonObject { ["enum"] = new JsonArray(code.Extra.Select(e => (JsonNode)e).ToArray()) }) };
    }

    /// <summary>A string field gets the code; a list of strings gets it for its items. Null stays allowed where it was.</summary>
    private static JsonNode WithCode(JsonNode node, Code code)
    {
        if (node is JsonObject list && list["items"] is JsonObject)
        {
            list["items"] = CodeNode(code);
            return list;
        }
        return IsNullable(node)
            ? new JsonObject { ["oneOf"] = new JsonArray(CodeNode(code), new JsonObject { ["type"] = "null" }) }
            : CodeNode(code);
    }
}
