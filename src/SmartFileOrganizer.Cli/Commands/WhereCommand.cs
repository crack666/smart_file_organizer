using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Places;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo where &lt;path-or-hash&gt;</c>: where a file lies today, where it lay, and when and by which plan it moved
/// (https://github.com/crack666/smart_file_organizer/issues/86). From the move history, the stored scans and the move
/// notices they found; reads no file and calls no model.
/// </summary>
public static class WhereCommand
{
    public sealed record PlaceView(string Path, string Source, bool? Exists, long? JobId, DateTime? SeenAt);

    public sealed record MoveView(string From, string? To, DateTime MovedAt, long PlanId, long? ActionId, string State,
        DateTime? ReturnedAt, bool Purged, string Source);

    /// <param name="Kind"><c>path</c> or <c>hash</c>.</param>
    /// <param name="Now">Where it lies today as far as the database knows, newest first; empty when nothing is known.</param>
    /// <param name="History">How it moved, oldest first.</param>
    public sealed record WhereResult(string Query, string Kind, string? Hash, IReadOnlyList<PlaceView> Now, IReadOnlyList<MoveView> History);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var query = new Argument<string>("path-or-hash")
        {
            Description = "A path (a file or a folder: everything below it) or a full SHA-256 checksum (64 hex digits)."
        };
        var command = new Command("where",
            "Where a file lies today, where it lay, and when and by which plan it moved; from the move history and the stored scans.")
        {
            query
        };
        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var report = await services.GetRequiredService<PlaceService>().WhereAsync(ctx.ParseResult.GetValue(query)!, ct);
            var result = new WhereResult(report.Query, report.Kind, report.Hash,
                report.Now.Select(p => new PlaceView(p.Path, p.Source.ToCode(), p.Exists, p.JobId, p.SeenAt)).ToList(),
                report.History.Select(m => new MoveView(m.From, m.To, m.MovedAt, m.PlanId, m.ActionId, m.State.ToCode(),
                    m.ReturnedAt, m.Purged, m.Source.ToCode())).ToList());
            return CommandOutcome.Success(result, () => Render(result));
        });
        return command;
    }

    private static string Render(WhereResult r)
    {
        var sb = new StringBuilder();
        if (r.Now.Count == 0 && r.History.Count == 0)
            return $"Nothing is known about {r.Query}.";
        foreach (var p in r.Now)
            sb.AppendLine($"now:   {p.Path} ({p.Source}{(p.Exists == false ? ", not reachable now" : string.Empty)})");
        foreach (var m in r.History)
            sb.AppendLine($"{m.MovedAt:yyyy-MM-dd HH:mm} plan {m.PlanId}: {m.From} -> {m.To ?? "?"}"
                          + (m.State == MoveState.Returned.ToCode() ? $" (back since {m.ReturnedAt:yyyy-MM-dd HH:mm})" : string.Empty)
                          + (m.Purged ? " (original purged)" : string.Empty));
        return sb.ToString().TrimEnd();
    }
}
