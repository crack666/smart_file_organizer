using System.Text.Json;
using Json.Schema;

namespace SmartFileOrganizer.Application.Exchange;

/// <summary>
/// The JSON schemas of the exchange format, version 1, as published under <c>schemas/exchange/v1/</c> and embedded in
/// this assembly. The import checks every line against them; the tests check every exported line.
/// </summary>
public static class ExchangeSchemas
{
    public const string JobSchema = "job.schema.json";
    public const string DirectorySchema = "directory.schema.json";
    public const string FileSchema = "file.schema.json";

    /// <summary>A line of <c>findings.jsonl</c>, since 1.1.</summary>
    public const string FindingSchema = "finding.schema.json";

    /// <summary>A line of <c>plan.jsonl</c>, since 1.1.</summary>
    public const string PlanActionSchema = "plan-action.schema.json";

    public static IReadOnlyList<string> Names { get; } = [JobSchema, DirectorySchema, FileSchema, FindingSchema, PlanActionSchema];

    private static readonly Lazy<IReadOnlyDictionary<string, JsonSchema>> Schemas = new(() =>
    {
        // A registry of its own: the global one would refuse the same $id a second time (tests build it more than once).
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        return Names.ToDictionary(n => n, n => JsonSchema.FromText(Text(n), options));
    });

    /// <summary>The text of an embedded schema file.</summary>
    public static string Text(string name)
    {
        var resource = $"SmartFileOrganizer.Application.Exchange.v{ExchangeFormat.Major}.{name}";
        using var stream = typeof(ExchangeSchemas).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Schema {resource} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Checks <paramref name="instance"/> against the schema <paramref name="name"/>. Returns the problems, each with
    /// the location in the instance (<c>/result/category: ...</c>); empty when the instance is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(string name, JsonElement instance) => Problems(Schemas.Value[name], instance);

    /// <summary>
    /// Checks <paramref name="instance"/> against another schema that may refer to the exchange schemas by their
    /// <c>$id</c> (the result schemas of the CLI under <c>schemas/cli/</c>). Same output as <see cref="Validate"/>.
    /// </summary>
    public static IReadOnlyList<string> ValidateWith(string schemaText, JsonElement instance)
    {
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        foreach (var name in Names)
            JsonSchema.FromText(Text(name), options);
        return Problems(JsonSchema.FromText(schemaText, options), instance);
    }

    private static IReadOnlyList<string> Problems(JsonSchema schema, JsonElement instance)
    {
        var results = schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (results.IsValid) return [];

        var problems = new List<string>();
        foreach (var detail in results.Details ?? [])
        {
            if (detail.Errors is null) continue;
            foreach (var (_, message) in detail.Errors)
                problems.Add($"{(detail.InstanceLocation.ToString() is { Length: > 0 } at ? at : "/")}: {message}");
        }
        if (problems.Count == 0 && results.Errors is { } top)
            problems.AddRange(top.Select(e => $"/: {e.Value}"));
        if (problems.Count == 0)
            problems.Add("/: does not match the schema");
        return problems.Distinct().ToList();
    }
}
