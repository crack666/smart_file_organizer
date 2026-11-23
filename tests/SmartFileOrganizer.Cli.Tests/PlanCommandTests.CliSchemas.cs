using System.Text.Json;
using System.Text.Json.Nodes;
using SmartFileOrganizer.Application.Exchange;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// The <c>result</c> of <c>files</c>, <c>dirs</c>, <c>show</c> and <c>summary</c> against the published schemas under
/// <c>schemas/cli/v1/</c> (https://github.com/crack666/smart_file_organizer/issues/116), on the generated source of this
/// class: verdicts, results with certainty and markers, a comparison, bundles and findings. Code fields refer to the
/// exchange schemas, so a category in the CLI and in an export are the same code.
/// </summary>
public partial class PlanCommandTests
{
    [Fact]
    public async Task ReadCommands_ResultMatchesThePublishedSchema_WithTheCodesOfTheExchangeFormat()
    {
        var (job, _) = await PrepareAsync();

        var outputs = new (string Schema, JsonElement Result)[]
        {
            ("files.result.schema.json", await OkAsync("files", job, "--limit", "500")),
            ("dirs.result.schema.json", await OkAsync("dirs", job)),
            ("show.result.schema.json", await OkAsync("show", job, "Downloads/IMG_20230812_143012.jpg")),
            ("show.result.schema.json", await OkAsync("show", job, "Privat/passwoerter.txt")),
            ("show.result.schema.json", await OkAsync("show", job, "Downloads")),
            ("show.result.schema.json", await OkAsync("show", job, ".")),
            ("summary.result.schema.json", await OkAsync("summary", job))
        };
        foreach (var (schema, result) in outputs)
        {
            var problems = ExchangeSchemas.ValidateWith(CliSchemaTests.Published(schema), result);
            Assert.True(problems.Count == 0, $"{schema}: {string.Join("; ", problems)}\n{result.GetRawText()}");
        }

        // The same codes as files.jsonl: an export of the job and files --json agree file by file.
        var folder = Path.Combine(_dir, "codes");
        await OkAsync("export", job, "--format", "jsonl", "--out", folder);
        var exported = File.ReadAllLines(Path.Combine(folder, ExchangeFormat.FilesFile))
            .Select(l => JsonNode.Parse(l)!).ToDictionary(l => l["name"]!.GetValue<string>());
        var listed = outputs[0].Result.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(exported.Count, listed.Count);
        foreach (var item in listed)
        {
            var line = exported[item.GetProperty("name").GetString()!];
            Assert.Equal(line["file_type"]!.GetValue<string>(), item.GetProperty("file_type").GetString());
            Assert.Equal(line["status"]!.GetValue<string>(), item.GetProperty("status").GetString());
            Assert.Equal(line["result"]?["category"]?.GetValue<string>(), item.GetProperty("category").GetString());
        }

        // An enum name where a code belongs is refused.
        var wrong = JsonNode.Parse(outputs[0].Result.GetRawText())!;
        wrong["items"]![0]!["category"] = "ReviewNeeded";
        using var wrongDoc = JsonDocument.Parse(wrong.ToJsonString());
        Assert.NotEmpty(ExchangeSchemas.ValidateWith(CliSchemaTests.Published("files.result.schema.json"), wrongDoc.RootElement));
        var extra = JsonNode.Parse(outputs[^1].Result.GetRawText())!;
        extra["by_category"]!["Holiday"] = 1;
        using var extraDoc = JsonDocument.Parse(extra.ToJsonString());
        Assert.NotEmpty(ExchangeSchemas.ValidateWith(CliSchemaTests.Published("summary.result.schema.json"), extraDoc.RootElement));
    }
}
