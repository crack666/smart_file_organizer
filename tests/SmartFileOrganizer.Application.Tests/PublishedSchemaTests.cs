using System.Text.Json;
using System.Text.Json.Nodes;
using SmartFileOrganizer.Application.Exchange;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The published schemas under <c>schemas/</c> (https://github.com/crack666/smart_file_organizer/issues/34): the answer
/// schemas of the model are the ones the prompt sends, and their fixed values are the ones the parser reads; the
/// exchange schemas list the codes of the enums.
/// </summary>
/// <remarks>
/// After a deliberate change of an answer schema, write the files again with <c>SFO_WRITE_SCHEMAS=1 dotnet test
/// --filter PublishedSchemaTests</c> and commit them with the change.
/// </remarks>
public class PublishedSchemaTests
{
    private static readonly string[] MetaKeys = ["$schema", "$id", "title", "description"];

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SmartFileOrganizer.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Repository root (SmartFileOrganizer.slnx) not found above the test output.");
    }

    private static string ModelFolder => Path.Combine(RepoRoot(), "schemas", "model");

    private static JsonObject Published(string file) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(ModelFolder, file)))!.AsObject();

    private static JsonNode Sent(object schema) => JsonSerializer.SerializeToNode(schema)!;

    [Fact]
    public void AnswerSchemas_AreTheOnesThePromptSends()
    {
        var write = Environment.GetEnvironmentVariable("SFO_WRITE_SCHEMAS") == "1";
        foreach (var (file, title, schema) in OllamaService.PublishedAnswerSchemas())
        {
            var path = Path.Combine(ModelFolder, file);
            if (write)
            {
                var head = new JsonObject
                {
                    ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                    ["$id"] = $"https://github.com/crack666/smart_file_organizer/schemas/model/{file}",
                    ["title"] = title,
                    ["description"] = "Sent to the model server as the format of the answer (Ollama 'format', OpenAI 'response_format'). " +
                                      "Written from the code by PublishedSchemaTests; do not edit by hand."
                };
                foreach (var (key, value) in Sent(schema).AsObject())
                    head[key] = value?.DeepClone();
                Directory.CreateDirectory(ModelFolder);
                File.WriteAllText(path, head.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }) + "\n");
            }

            Assert.True(File.Exists(path), $"{file} is missing; write it with SFO_WRITE_SCHEMAS=1.");
            var published = Published(file);
            foreach (var key in MetaKeys) published.Remove(key);
            Assert.True(JsonNode.DeepEquals(Sent(schema), published),
                $"{file} differs from the schema the prompt sends; write it again with SFO_WRITE_SCHEMAS=1.");
        }
    }

    [Fact]
    public void ModelFolder_HoldsNoOtherSchema()
    {
        var expected = OllamaService.PublishedAnswerSchemas().Select(s => s.File).Order(StringComparer.Ordinal);
        var present = Directory.GetFiles(ModelFolder, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(expected, present);
    }

    private static IReadOnlyList<string> Enum(JsonObject schema, string property) =>
        schema["properties"]![property]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();

    private static FileClassification Parse(object answer)
    {
        var body = JsonSerializer.Serialize(new { message = new { content = JsonSerializer.Serialize(answer) } });
        var result = new FileClassification();
        OllamaService.ParseResponse(body, result);
        return result;
    }

    [Fact]
    public void Category_TheSchemaListsExactlyWhatTheParserReads()
    {
        var listed = Enum(Published("phase2-classification.schema.json"), "category");

        // Both sides come from FileCategory: a new value appears in the schema and is read by the parser.
        Assert.Equal(System.Enum.GetNames<FileCategory>().Order(), listed.Order());
        foreach (var name in listed)
        {
            var parsed = Parse(new { category = name, importance = "Low", confidence = 0.5, summary = "s", sensitive = Array.Empty<string>() });
            Assert.Equal(name, parsed.Category.ToString());
        }

        Assert.Equal(FileCategory.Unknown, Parse(new { category = "Holiday", importance = "Low", confidence = 0.5, summary = "s" }).Category);
    }

    [Fact]
    public void Importance_TheSchemaListsExactlyWhatTheParserReads()
    {
        var listed = Enum(Published("phase2-classification.schema.json"), "importance");

        Assert.Equal(System.Enum.GetNames<Importance>().Order(), listed.Order());
        foreach (var name in listed)
            Assert.Equal(name, Parse(new { category = "Photo", importance = name, confidence = 0.5, summary = "s" }).Importance.ToString());
        Assert.Equal(Importance.Unknown, Parse(new { category = "Photo", importance = "Huge", confidence = 0.5, summary = "s" }).Importance);
    }

    [Fact]
    public void Markers_KeepHintAndVerdict_ListTheCodesTheParsersRead()
    {
        var classification = Published("phase2-classification-mixed-folder.schema.json");
        var markers = classification["properties"]!["sensitive"]!["items"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
        Assert.Equal(SensitiveMarkerNames.AllTokens, markers);
        foreach (var token in markers)
        {
            var parsed = Parse(new { category = "Document", importance = "Low", confidence = 0.5, summary = "s", sensitive = new[] { token } });
            Assert.Equal(token, Assert.Single(SensitiveMarkerNames.ToTokens(parsed.SensitiveMarkers)));
        }

        Assert.Equal(VerdictCodes.All<KeepHint>(), Enum(classification, "keep_hint"));

        var verdict = Published("phase1-directory-verdict.schema.json");
        Assert.Equal(DirectoryVerdictKinds.ModelCodes, Enum(verdict, "verdict"));
        foreach (var code in Enum(verdict, "verdict"))
            Assert.True(PreAssessmentResponseParser.TryParseVerdict(code, out var kind) && kind.ToCode() == code, code);
        Assert.Equal(VerdictCodes.All<RetentionValue>(), Enum(verdict, "retention"));
        foreach (var code in Enum(verdict, "retention"))
            Assert.True(PreAssessmentResponseParser.TryParseRetention(code, out var value) && value.ToCode() == code, code);
    }

    [Fact]
    public void PromptFingerprint_IsUnchangedByWritingTheListsFromTheEnums()
    {
        // The lists were literal arrays until 22.11.2026; the same order keeps the content store's prompt version.
        var json = JsonSerializer.Serialize(OllamaService.BuildClassificationSchema(askKeepHint: false));
        Assert.Contains("""
            "enum":["Photo","DocumentScan","Screenshot","Video","Audio","Document","Invoice","PersonalDocument","Letter","Archive","Code","SoftwareInstaller","SystemFile","TrashCandidate","ReviewNeeded","Unknown"]
            """, json);
        Assert.Contains("""
            "enum":["Low","Medium","High","Critical","Unknown"]
            """, json);
    }

    // ------------------------------------------------------------------ exchange format

    /// <summary>The codes a schema allows at a <c>$defs</c> entry: <c>enum</c> or the <c>const</c> of each <c>oneOf</c>.</summary>
    private static IReadOnlyList<string> Codes(string schemaFile, string def)
    {
        var node = JsonNode.Parse(ExchangeSchemas.Text(schemaFile))!["$defs"]![def]!;
        var values = node["enum"] is JsonArray e ? e : new JsonArray(node["oneOf"]!.AsArray().Select(o => o!["const"]!.DeepClone()).ToArray());
        return values.Select(v => v!.GetValue<string>()).ToList();
    }

    public static TheoryData<string, string, string[]> ExchangeCodes() => new()
    {
        { ExchangeSchemas.FileSchema, "FileCategory", [.. ExchangeFormat.SnakeCodes<FileCategory>()] },
        { ExchangeSchemas.FileSchema, "Importance", [.. ExchangeFormat.SnakeCodes<Importance>()] },
        { ExchangeSchemas.FileSchema, "FileType", [.. ExchangeFormat.SnakeCodes<FileType>()] },
        { ExchangeSchemas.FileSchema, "FileNodeStatus", [.. ExchangeFormat.SnakeCodes<FileNodeStatus>()] },
        { ExchangeSchemas.FileSchema, "ResultOrigin", [.. VerdictCodes.All<ResultOrigin>()] },
        { ExchangeSchemas.FileSchema, "ResultCertainty", [.. VerdictCodes.All<ResultCertainty>()] },
        { ExchangeSchemas.FileSchema, "SensitiveMarker", [.. SensitiveMarkerNames.AllTokens] },
        { ExchangeSchemas.FileSchema, "KeepHint", [.. VerdictCodes.All<KeepHint>()] },
        { ExchangeSchemas.FileSchema, "FileFactKey", [.. VerdictCodes.All<FileFactKey>()] },
        { ExchangeSchemas.FileSchema, "FactSource", [.. VerdictCodes.All<FactSource>()] },
        { ExchangeSchemas.FileSchema, "ContentState", [.. ContentStateValues.All] },
        { ExchangeSchemas.FileSchema, "ScanErrorCause", [.. VerdictCodes.All<ScanErrorCause>()] },
        { ExchangeSchemas.DirectorySchema, "DirectoryVerdictKind", [.. VerdictCodes.All<DirectoryVerdictKind>()] },
        { ExchangeSchemas.DirectorySchema, "RetentionValue", [.. VerdictCodes.All<RetentionValue>()] },
        { ExchangeSchemas.DirectorySchema, "VerdictSource", [.. VerdictCodes.All<VerdictSource>()] },
        { ExchangeSchemas.DirectorySchema, "DirectoryStatus", [.. VerdictCodes.All<DirectoryStatus>()] },
        { ExchangeSchemas.DirectorySchema, "LinkKind", [.. VerdictCodes.All<LinkKind>()] },
        { ExchangeSchemas.DirectorySchema, "SensitiveMarker", [.. SensitiveMarkerNames.AllTokens] },
        { ExchangeSchemas.JobSchema, "ScanRole", [ScanRoleValues.Source, ScanRoleValues.Library] },
        { ExchangeSchemas.JobSchema, "JobStatus", [.. ExchangeFormat.SnakeCodes<JobStatus>()] }
    };

    [Theory]
    [MemberData(nameof(ExchangeCodes))]
    public void ExchangeSchema_ListsTheCodesOfTheEnum(string schemaFile, string def, string[] codes)
    {
        Assert.Equal(codes, Codes(schemaFile, def));
    }

    [Fact]
    public void ExchangeSchemas_InTheRepositoryAreTheEmbeddedOnes()
    {
        foreach (var name in ExchangeSchemas.Names)
        {
            var file = File.ReadAllText(Path.Combine(RepoRoot(), "schemas", "exchange", $"v{ExchangeFormat.Major}", name));
            Assert.Equal(file.ReplaceLineEndings("\n"), ExchangeSchemas.Text(name).ReplaceLineEndings("\n"));
        }
    }

    [Fact]
    public void Validate_NamesTheLocationOfAWrongCode()
    {
        using var doc = JsonDocument.Parse("""{"relative_path":"a.txt","result":{"category":"Holiday"}}""");

        var problems = ExchangeSchemas.Validate(ExchangeSchemas.FileSchema, doc.RootElement);

        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.StartsWith("/result", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RequiresATimeWithOffset()
    {
        using var ok = JsonDocument.Parse("""{"relative_path":"a.txt","last_write_time":"2026-11-22T10:00:00Z"}""");
        using var bad = JsonDocument.Parse("""{"relative_path":"a.txt","last_write_time":"22.11.2026"}""");

        Assert.Empty(ExchangeSchemas.Validate(ExchangeSchemas.FileSchema, ok.RootElement));
        Assert.NotEmpty(ExchangeSchemas.Validate(ExchangeSchemas.FileSchema, bad.RootElement));
    }
}
