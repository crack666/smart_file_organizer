using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Media;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo facts refresh &lt;job&gt;</c>: reads the video facts of a stored job again from container, name and companion
/// files (https://github.com/crack666/smart_file_organizer/issues/103), and since 23.11.2026 the origin of its images and
/// the kind of its audio files. Never calls the model server and needs no GPU; the analysis of the job stays as it is.
/// </summary>
public static class FactsCommand
{
    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("facts", "Facts read from the files themselves.");
        command.Subcommands.Add(CreateRefresh(host, globals));
        return command;
    }

    private static Command CreateRefresh(CliHost host, GlobalOptions globals)
    {
        var job = JobArguments.Create("Job id (see sfo jobs): a source or a library scan.");
        var command = new Command("refresh",
            "Read video kind, device, title, year, season and episode again from container, name and companion files, " +
            "and the origin of images (image_origin) and the kind of audio files (audio_kind). No model call. " +
            "With --verbose, each video with the evidence the rules looked at (field, value, effect).") { job };
        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var id = ctx.ParseResult.GetValue(job);
            if (await services.GetRequiredService<IScanJobRepository>().GetByIdAsync(id, ct) is null)
                throw new CliFailureException(ExitCodes.Failure, ErrorCodes.JobNotFound, $"Job {id} not found.");

            var verbose = ctx.ParseResult.GetValue(globals.Verbose);
            var result = await services.GetRequiredService<VideoFactRefreshService>().RefreshAsync(id, verbose, ct);
            return CommandOutcome.Success(result, () => Text(result));
        });
        return command;
    }

    private static string Text(VideoFactRefreshResult result)
    {
        string Kind(KeyValuePair<string, int> k) =>
            result.Certainty?.GetValueOrDefault(k.Key) is { Count: > 0 } c
                ? $"{k.Key} {k.Value} ({string.Join(", ", c.Select(x => $"{x.Value} {x.Key}"))})"
                : $"{k.Key} {k.Value}";
        var text = $"job {result.JobId}: {result.Read} of {result.Videos} video(s) read, {result.Missing} missing" +
            (result.Kinds.Count == 0 ? string.Empty : " - " + string.Join(", ", result.Kinds.Select(Kind))) +
            (result.ModelKindsKept > 0 ? $"; {result.ModelKindsKept} kind(s) kept from the model" : string.Empty) +
            (result.Disputed > 0 ? $"; {result.Disputed} disputed" : string.Empty) +
            Media("image(s)", result.Images) + Media("audio file(s)", result.Audio);
        if (result.Files is null) return text;

        var sb = new System.Text.StringBuilder(text);
        foreach (var f in result.Files)
        {
            sb.AppendLine().Append($"{f.RelativePath}: {f.Kind} ({f.Source}{(f.Certainty is null ? string.Empty : ", " + f.Certainty)})");
            if (f.Device is not null) sb.Append($" {f.Device}{(f.DeviceCertainty is null ? string.Empty : $" ({f.DeviceCertainty})")}");
            if (f.RuleKind is not null) sb.Append($", rules read {f.RuleKind}");
            if (f.Reason is not null) sb.Append($" - {f.Reason}");
            foreach (var e in f.Evidence)
                sb.AppendLine().Append($"  {e.Field}{(e.Value is null ? string.Empty : $" '{e.Value}'")}: {e.Effect}");
        }
        return sb.ToString();
    }

    private static string Media(string what, MediaFactRefresh? m) =>
        m is null || m.Files == 0 ? string.Empty
            : $"; {m.Read} of {m.Files} {what} read, {m.Missing} missing" +
              (m.Kinds.Count == 0 ? string.Empty : " - " + string.Join(", ", m.Kinds.Select(k => $"{k.Key} {k.Value}"))) +
              (m.Open > 0 ? $", {m.Open} open" : string.Empty);
}
