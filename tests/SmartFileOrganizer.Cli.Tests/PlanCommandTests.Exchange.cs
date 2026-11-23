using System.Text.Json;
using System.Text.Json.Nodes;
using SmartFileOrganizer.Application.Exchange;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>findings.jsonl</c> and <c>plan.jsonl</c> of the exchange format, version 1.1
/// (https://github.com/crack666/smart_file_organizer/issues/116), on the generated source and library of this class: a
/// source analysed with scripted answers, compared, bundled, with targets, every finding accepted and a plan approved.
/// </summary>
public partial class PlanCommandTests
{
    [Fact]
    public async Task Export_FindingsAndPlan_EveryLineMatchesItsSchema_AndPointsIntoFilesAndDirectories()
    {
        var (job, library) = await PrepareAsync();
        var planId = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        await OkAsync("plan", "approve", planId, "--all");
        var folder = Path.Combine(_dir, "export");

        var result = await OkAsync("export", job, "--format", "jsonl", "--out", folder);

        Assert.Equal("1.1", result.GetProperty("format_version").GetString());
        Assert.Equal(["job.json", "directories.jsonl", "files.jsonl", "findings.jsonl", "plan.jsonl"],
            result.GetProperty("files").EnumerateArray().Select(f => f.GetString()));

        using var headDoc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, ExchangeFormat.JobFile)));
        var head = headDoc.RootElement;
        Assert.Empty(ExchangeSchemas.Validate(ExchangeSchemas.JobSchema, head));
        Assert.Equal(["directories.jsonl", "files.jsonl", "findings.jsonl", "plan.jsonl"],
            head.GetProperty("parts").EnumerateArray().Select(p => p.GetString()));

        var directories = ExchangeLines(Path.Combine(folder, ExchangeFormat.DirectoriesFile));
        var files = ExchangeLines(Path.Combine(folder, ExchangeFormat.FilesFile));
        var findings = ExchangeLines(Path.Combine(folder, ExchangeFormat.FindingsFile));
        var actions = ExchangeLines(Path.Combine(folder, ExchangeFormat.PlanFile));
        Assert.NotEmpty(findings);
        Assert.NotEmpty(actions);
        Assert.Equal(head.GetProperty("findings").GetInt32(), findings.Count);
        Assert.Equal(head.GetProperty("plan_actions").GetInt32(), actions.Count);

        foreach (var (schema, lines) in new[] { (ExchangeSchemas.FindingSchema, findings), (ExchangeSchemas.PlanActionSchema, actions) })
        {
            var keys = JsonNode.Parse(ExchangeSchemas.Text(schema))!["properties"]!.AsObject().Select(p => p.Key).ToList();
            foreach (var line in lines)
            {
                var problems = ExchangeSchemas.Validate(schema, line);
                Assert.True(problems.Count == 0, $"{line.GetRawText()}: {string.Join("; ", problems)}");
                Assert.Equal(keys, line.EnumerateObject().Select(p => p.Name).ToList());
            }
        }

        var fileIds = files.Select(f => f.GetProperty("id").GetInt64()).ToHashSet();
        var dirIds = directories.Select(d => d.GetProperty("id").GetInt64()).ToHashSet();
        var keysOfFindings = findings.Select(f => f.GetProperty("key").GetString()).ToHashSet();

        // Findings: in rank order, every file and folder they name is a line of the other files.
        Assert.Equal(Enumerable.Range(1, findings.Count), findings.Select(f => f.GetProperty("rank").GetInt32()));
        Assert.All(findings, f => Assert.All(f.GetProperty("file_ids").EnumerateArray(), id => Assert.Contains(id.GetInt64(), fileIds)));
        Assert.All(findings.Where(f => f.GetProperty("directory_id").ValueKind != JsonValueKind.Null),
            f => Assert.Contains(f.GetProperty("directory_id").GetInt64(), dirIds));
        Assert.All(findings, f => Assert.Equal("accepted", f.GetProperty("state").GetString()));
        var croatia = findings.Single(f => f.GetProperty("relative_path").GetString() == "Urlaub Kroatien 2021");
        Assert.Equal(4, croatia.GetProperty("file_ids").GetArrayLength());
        Assert.Equal("not_backed_up", croatia.GetProperty("kind").GetString());

        // Plan actions: each comes from a finding of the export and names its file or folder there.
        Assert.All(actions, a => Assert.Contains(a.GetProperty("finding_key").GetString(), keysOfFindings));
        Assert.All(actions, a => Assert.Equal(long.Parse(planId), a.GetProperty("plan_id").GetInt64()));
        foreach (var a in actions)
        {
            if (a.GetProperty("source_kind").GetString() == "file")
                Assert.Contains(a.GetProperty("file_id").GetInt64(), fileIds);
            else
                Assert.Contains(a.GetProperty("directory_id").GetInt64(), dirIds);
        }
        var folderAction = actions.Single(a => a.GetProperty("relative_path").GetString() == "Urlaub Kroatien 2021");
        Assert.Equal("folder", folderAction.GetProperty("source_kind").GetString());
        Assert.Equal("approved", folderAction.GetProperty("state").GetString());
        Assert.Equal(Path.Combine(_library, "Fotos", "Urlaub Kroatien 2021"), folderAction.GetProperty("target_path").GetString());
        var secret = actions.Single(a => a.GetProperty("sensitive").GetBoolean());
        Assert.Equal("proposed", secret.GetProperty("state").GetString());

        var plan = Assert.Single(head.GetProperty("plans").EnumerateArray());
        Assert.Equal(long.Parse(planId), plan.GetProperty("id").GetInt64());
        Assert.Equal(actions.Count, plan.GetProperty("actions").GetInt32());
        Assert.Equal([library], plan.GetProperty("library_job_ids").EnumerateArray().Select(l => l.GetInt64()));
    }

    [Fact]
    public async Task Export_OfALibrary_WritesNeitherFindingsNorPlan()
    {
        var library = (await OkAsync("scan", _library, "--role", "library")).GetProperty("job").GetProperty("id").GetInt64();
        var folder = Path.Combine(_dir, "library-export");

        var result = await OkAsync("export", library.ToString(), "--format", "jsonl", "--out", folder);

        Assert.Equal(["job.json", "directories.jsonl", "files.jsonl"], result.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("findings").ValueKind);
        Assert.False(File.Exists(Path.Combine(folder, ExchangeFormat.FindingsFile)));
        Assert.False(File.Exists(Path.Combine(folder, ExchangeFormat.PlanFile)));
        using var head = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, ExchangeFormat.JobFile)));
        Assert.Empty(ExchangeSchemas.Validate(ExchangeSchemas.JobSchema, head.RootElement));
        Assert.Equal(JsonValueKind.Null, head.RootElement.GetProperty("findings").ValueKind);
        Assert.Empty(head.RootElement.GetProperty("plans").EnumerateArray());
    }

    private static List<JsonElement> ExchangeLines(string path) =>
        File.ReadAllLines(path).Where(l => l.Length > 0).Select(l =>
        {
            using var doc = JsonDocument.Parse(l);
            return doc.RootElement.Clone();
        }).ToList();
}
