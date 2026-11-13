using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo cache stats</c> and <c>sfo cache clear</c>: the content store, assessments by content across jobs
/// (https://github.com/crack666/smart_file_organizer/issues/38). Neither command calls the model server: the current
/// model and prompt version come from the settings.
/// </summary>
public static class CacheCommand
{
    /// <summary>Entries made with one model and prompt version; <c>Current</c> when they are taken over today.</summary>
    public sealed record GroupView(string Model, string PromptVersion, bool Current, long Entries, long Reuses, DateTime? LastUsedAt);

    /// <param name="Stale">Entries of another model or prompt version: kept, but no longer taken over.</param>
    /// <param name="Fingerprints">Files with a full checksum in <c>content_fingerprints</c> (shared with <c>sfo compare</c>).</param>
    public sealed record StatsResult(string? Model, string? PromptVersion, long Entries, long Current, long Stale, long Reuses,
        long Corrections, long Fingerprints, IReadOnlyList<GroupView> Groups);

    public sealed record ClearResult(bool OnlyStale, long EntriesRemoved, long CorrectionsRemoved, long EntriesLeft, long CorrectionsLeft);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("cache", "Assessments kept by content across jobs (content store).");
        command.Subcommands.Add(CreateStats(host, globals));
        command.Subcommands.Add(CreateClear(host, globals));
        return command;
    }

    private static Command CreateStats(CliHost host, GlobalOptions globals)
    {
        var command = new Command("stats", "Count the entries of the content store, current and stale, and how often they were taken over.");
        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var current = services.GetRequiredService<IOllamaService>().AssessmentIdentity;
            var stats = await services.GetRequiredService<ContentStoreService>().GetStatsAsync(current, ct);
            var result = Map(current, stats);
            return CommandOutcome.Success(result, () => Render(result));
        });
        return command;
    }

    private static Command CreateClear(CliHost host, GlobalOptions globals)
    {
        var stale = new Option<bool>("--stale")
        {
            Description = "Only remove entries of another model or prompt version; the current ones stay."
        };
        var corrections = new Option<bool>("--corrections")
        {
            Description = "Also remove the corrections a person made by content (the corrections of single files stay)."
        };
        var command = new Command("clear", "Remove entries of the content store; the next analysis asks the model again.") { stale, corrections };
        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var current = services.GetRequiredService<IOllamaService>().AssessmentIdentity;
            var store = services.GetRequiredService<ContentStoreService>();
            var onlyStale = ctx.ParseResult.GetValue(stale);
            var (entries, removedCorrections) = await store.ClearAsync(current, onlyStale, ctx.ParseResult.GetValue(corrections), ct);
            var left = await store.GetStatsAsync(current, ct);
            var result = new ClearResult(onlyStale, entries, removedCorrections, left.Entries, left.Corrections);
            return CommandOutcome.Success(result, () =>
                $"Removed {entries} entr{(entries == 1 ? "y" : "ies")}{(onlyStale ? " of other models or prompt versions" : string.Empty)}" +
                $" and {removedCorrections} correction(s); {left.Entries} entr{(left.Entries == 1 ? "y" : "ies")} and {left.Corrections} correction(s) left.");
        });
        return command;
    }

    public static StatsResult Map(AssessmentIdentity? current, ContentStoreStats stats) => new(
        current?.Model, current?.PromptVersion, stats.Entries, stats.Current, stats.Stale, stats.Reuses, stats.Corrections,
        stats.Fingerprints,
        stats.Groups.Select(g => new GroupView(g.Model, g.PromptVersion,
            current is not null && g.Model == current.Model && g.PromptVersion == current.PromptVersion,
            g.Entries, g.Reuses, g.LastUsedAt)).ToList());

    private static string Render(StatsResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"content store     {r.Entries} entries: {r.Current} current, {r.Stale} stale (other model or prompt version)");
        sb.AppendLine($"  taken over      {r.Reuses} time(s) instead of a model call");
        sb.AppendLine($"  corrections     {r.Corrections} by content");
        sb.AppendLine($"  checksums       {r.Fingerprints} files");
        sb.AppendLine($"  current         {r.Model ?? "-"} / prompt {r.PromptVersion ?? "-"}");
        foreach (var g in r.Groups)
            sb.AppendLine($"    {(g.Current ? "*" : " ")} {g.Model} / {g.PromptVersion}: {g.Entries} entries, {g.Reuses} taken over" +
                          (g.LastUsedAt is { } at ? $", last {at:yyyy-MM-dd HH:mm}Z" : string.Empty));
        return sb.ToString().TrimEnd();
    }
}
