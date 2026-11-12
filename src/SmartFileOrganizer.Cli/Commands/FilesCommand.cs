using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary><c>sfo files &lt;job&gt;</c>: files with type, category, confidence, summary and status, one page at a time.</summary>
public static class FilesCommand
{
    /// <summary>Extra <c>--status</c> value: discovered or processing, i.e. still waiting for classification.</summary>
    public const string PendingStatus = "pending";

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs).");
        var read = new ReadOptions();
        var dir = new Option<string?>("--dir")
        {
            Description = "Only files directly in this directory (full path, or relative to the job root).",
            HelpName = "path"
        };
        var recursive = new Option<bool>("--recursive", "-r")
        {
            Description = "With --dir: include files in its subdirectories."
        };
        var bundle = new Option<long?>("--bundle")
        {
            Description = "Only files of this bundle and its child bundles (id from sfo bundles list).",
            HelpName = "id"
        };
        var category = ReadFormat.EnumOption<FileCategory>("--category",
            "Category as shown in the output (unknown also matches unclassified files)");
        var status = ReadFormat.EnumOption<FileNodeStatus>("--status", "File status", PendingStatus);
        var type = ReadFormat.EnumOption<FileType>("--type", "File type");
        // A string, parsed with the invariant culture: a typed double option follows the machine's culture
        // and read "0.5" as 5 on a German system.
        var maxConfidence = new Option<string?>("--max-confidence")
        {
            Description = "Only files the model classified with at most this confidence (0 to 1, decimal point). Files without a confidence are left out.",
            HelpName = "value"
        };
        maxConfidence.Validators.Add(r =>
        {
            var text = r.GetValueOrDefault<string?>();
            if (text is not null && ParseConfidence(text) is not (>= 0 and <= 1))
                r.AddError("--max-confidence must be a number between 0 and 1, for example 0.5.");
        });
        var certainty = ReadFormat.EnumOption<ResultCertainty>("--certainty",
            "How far the result can be relied on (review: two answers disagree, a person should look)");
        var sort = new Option<string>("--sort")
        {
            Description = "path or size (largest first).",
            DefaultValueFactory = _ => "path"
        };
        sort.AcceptOnlyFromAmong("path", "size");

        var command = new Command("files", "List files of a job with type, category, confidence, summary and status.")
        {
            job, dir, recursive, bundle, category, status, type, maxConfidence, certainty, sort
        };
        foreach (var option in read.Filters.Concat(read.Paging).Append(read.IncludeSensitive))
            command.Options.Add(option);

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;

            var statusValues = p.GetValue(status) ?? [];
            var statuses = ReadFormat.ParseEnums<FileNodeStatus>(statusValues);
            if (statusValues.Contains(PendingStatus, StringComparer.OrdinalIgnoreCase))
                statuses.AddRange([FileNodeStatus.Discovered, FileNodeStatus.Processing]);

            var query = read.Apply(new FileListQuery
            {
                JobId = p.GetValue(job),
                Directory = p.GetValue(dir),
                Recursive = p.GetValue(recursive),
                Bundle = p.GetValue(bundle),
                Categories = ReadFormat.ParseEnums<FileCategory>(p.GetValue(category)),
                Statuses = statuses.Distinct().ToList(),
                Types = ReadFormat.ParseEnums<FileType>(p.GetValue(type)),
                MaxConfidence = p.GetValue(maxConfidence) is { } text ? ParseConfidence(text) : null,
                Certainties = ReadFormat.ParseEnums<ResultCertainty>(p.GetValue(certainty)),
                Sort = p.GetValue(sort) == "size" ? FileSort.Size : FileSort.Path
            }, ctx);

            ReadPage<FileItem> page;
            try
            {
                page = await services.GetRequiredService<JobReadService>().ListFilesAsync(query, ct);
            }
            catch (JobReadException ex)
            {
                throw ReadFormat.ToFailure(ex);
            }

            var result = PageView<FileListView>.From(query.JobId, page, FileListView.From);
            var language = ReadFormat.Language(services);
            return Contract.CommandOutcome.Success(result, () => Render(result, page, language));
        });

        return command;
    }

    private static double? ParseConfidence(string text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string Render(PageView<FileListView> result, ReadPage<FileItem> page, DisplayLanguage language)
    {
        var sb = new StringBuilder();
        foreach (var f in result.Items)
            sb.AppendLine(f.ToLine(language));
        sb.Append(ReadFormat.PageFooter(page));
        return sb.ToString();
    }
}
