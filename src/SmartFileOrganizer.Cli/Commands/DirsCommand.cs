using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary><c>sfo dirs &lt;job&gt;</c>: directories with subtree size, file count, verdict and summary, one page at a time.</summary>
public static class DirsCommand
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs).");
        var read = new ReadOptions();
        var under = new Option<string?>("--under")
        {
            Description = "Only this directory and its subdirectories (full path, or relative to the job root).",
            HelpName = "path"
        };
        var depth = new Option<int?>("--depth")
        {
            Description = "Levels below --under (or the job root); 0 lists only that directory.",
            HelpName = "n"
        };
        depth.Validators.Add(r =>
        {
            if (r.GetValueOrDefault<int?>() is < 0)
                r.AddError("--depth must not be negative.");
        });
        var minSize = new Option<string?>("--min-size")
        {
            Description = "Minimum subtree size: bytes or a number with K, M, G, T (for example 500M).",
            HelpName = "size"
        };
        minSize.Validators.Add(r =>
        {
            var value = r.GetValueOrDefault<string?>();
            if (value is not null && !ReadFormat.TryParseSize(value, out _))
                r.AddError($"--min-size: cannot read '{value}' as a size.");
        });
        var sort = new Option<string>("--sort")
        {
            Description = "size (largest first), files (most first) or path.",
            DefaultValueFactory = _ => "path"
        };
        sort.AcceptOnlyFromAmong("size", "files", "path");

        var command = new Command("dirs", "List directories of a job with size, file count, verdict and summary.")
        {
            job, under, depth, minSize, sort
        };
        foreach (var option in read.Filters.Concat(read.Paging).Append(read.IncludeSensitive))
            command.Options.Add(option);

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var p = ctx.ParseResult;
            ReadFormat.TryParseSize(p.GetValue(minSize), out var minBytes);

            var query = read.Apply(new DirectoryListQuery
            {
                JobId = p.GetValue(job),
                Under = p.GetValue(under),
                MaxDepth = p.GetValue(depth),
                MinSize = p.GetValue(minSize) is null ? null : minBytes,
                Sort = p.GetValue(sort) switch
                {
                    "size" => DirectorySort.Size,
                    "files" => DirectorySort.Files,
                    _ => DirectorySort.Path
                }
            }, ctx);

            ReadPage<DirectoryItem> page;
            try
            {
                page = await services.GetRequiredService<JobReadService>().ListDirectoriesAsync(query, ct);
            }
            catch (JobReadException ex)
            {
                throw ReadFormat.ToFailure(ex);
            }

            var result = PageView<DirectoryView>.From(query.JobId, page, DirectoryView.From);
            return Contract.CommandOutcome.Success(result, () => Render(result, page));
        });

        return command;
    }

    private static string Render(PageView<DirectoryView> result, ReadPage<DirectoryItem> page)
    {
        var sb = new StringBuilder();
        foreach (var d in result.Items)
            sb.AppendLine(d.ToLine());
        sb.Append(ReadFormat.PageFooter(page));
        return sb.ToString();
    }
}
