using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Places;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>
/// <c>sfo places</c>: every known place with the age of its last scan and a short report
/// (https://github.com/crack666/smart_file_organizer/issues/86). Works on the stored state: no model, and the drive
/// need not be attached.
/// </summary>
public static class PlacesCommand
{
    public sealed record TallyView(long Files, long Bytes);

    public sealed record ReportView(TallyView WorthKeepingNotBackedUp, TallyView BackedUp, TallyView Disposable, TallyView Sensitive);

    public sealed record VolumeView(string? Label, string? Id, string? Format);

    /// <param name="Reachable">Whether the root can be reached now.</param>
    /// <param name="AgeDays">Whole days since the last completed scan.</param>
    /// <param name="Report">Null for a library or without a job; <c>report_note</c> says why.</param>
    public sealed record PlaceItem(long Id, string RootPath, string Role, VolumeView Volume, long? LastJobId, DateTime? LastScanAt,
        int? AgeDays, bool Reachable, long Files, long Bytes, ReportView? Report, string? ReportNote, DateTime FirstSeenAt);

    public sealed record PlacesResult(IReadOnlyList<PlaceItem> Places);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("places",
            "Known places (scanned roots) with the age of their last scan and a short report; works without the drive attached.");
        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = await ctx.GetServicesAsync(ct);
            var views = await services.GetRequiredService<PlaceService>().ListAsync(ct);
            var result = new PlacesResult(views.Select(Map).ToList());
            return CommandOutcome.Success(result, () => Render(result));
        });
        return command;
    }

    private static PlaceItem Map(PlaceView v) => new(
        v.Place.Id, v.Place.RootPath, v.Place.Role.ToValue(),
        new VolumeView(v.Place.VolumeLabel, v.Place.VolumeId, v.Place.VolumeFormat),
        v.Place.LastJobId, v.Place.LastScanAt, v.AgeDays, v.Reachable, v.Place.Files, v.Place.Bytes,
        v.Report is { } r ? new ReportView(T(r.WorthKeepingNotBackedUp), T(r.BackedUp), T(r.Disposable), T(r.Sensitive)) : null,
        v.ReportNote, v.Place.FirstSeenAt);

    private static TallyView T(FileTally t) => new(t.Files, t.Bytes);

    private static string Render(PlacesResult r)
    {
        if (r.Places.Count == 0)
            return "No known places yet; sfo scan <path> makes one.";
        var sb = new StringBuilder();
        foreach (var p in r.Places)
        {
            var volume = string.Join(", ", new[] { p.Volume.Label, p.Volume.Format, p.Volume.Id }.Where(x => x is not null));
            sb.AppendLine($"{p.Id,4} {p.Role,-7} {p.RootPath}" + (volume.Length > 0 ? $" [{volume}]" : string.Empty)
                          + (p.Reachable ? string.Empty : " (not reachable now)"));
            sb.AppendLine($"     last scan: job {p.LastJobId?.ToString() ?? "-"}, {(p.AgeDays is { } d ? $"{d} days ago" : "never")}; "
                          + $"{p.Files} files, {ReadFormat.Size(p.Bytes)}");
            if (p.Report is { } rep)
                sb.AppendLine($"     worth keeping, not backed up: {rep.WorthKeepingNotBackedUp.Files} files {ReadFormat.Size(rep.WorthKeepingNotBackedUp.Bytes)}; "
                              + $"backed up: {rep.BackedUp.Files} files {ReadFormat.Size(rep.BackedUp.Bytes)}; "
                              + $"disposable: {rep.Disposable.Files} files {ReadFormat.Size(rep.Disposable.Bytes)}");
        }
        return sb.ToString().TrimEnd();
    }
}
