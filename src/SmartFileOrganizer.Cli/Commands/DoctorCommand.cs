using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Hosting;
using SmartFileOrganizer.Infrastructure.ModelServer;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary><c>sfo doctor</c>: checks database, Ollama reachability and the configured model.</summary>
public static class DoctorCommand
{
    /// <summary>How long the reachability check waits for Ollama.</summary>
    public static readonly TimeSpan OllamaTimeout = TimeSpan.FromSeconds(5);

    public sealed record DatabaseCheck(string Path, bool Existed, int? SchemaVersion, bool Ok, string? Error);

    public sealed record OllamaCheck(string BaseUrl, bool Reachable);

    public sealed record ModelCheck(string Name, bool? Available, IReadOnlyList<string> Installed);

    /// <summary>One library root of a completed library job: can it be read right now (Issue 85)?</summary>
    public sealed record LibraryCheck(long JobId, string RootPath, bool Readable, string? Problem);

    public sealed record DoctorResult(bool Ok, DatabaseCheck Database, string SettingsPath, OllamaCheck Ollama, ModelCheck Model,
        IReadOnlyList<LibraryCheck>? Libraries = null, string Provider = "ollama", bool ApiKeyConfigured = false,
        bool Busy = false, int ParallelRequested = 1, int? ParallelMeasured = null, string? ParallelWarning = null);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("doctor", "Check the database, Ollama reachability, the configured model and whether the library roots can be read.");

        CommandRunner.SetHandler(command, host, globals, async (ctx, ct) =>
        {
            var services = ctx.BuildServices();
            var paths = services.GetRequiredService<SmartFileOrganizerPaths>();

            // Database: created and migrated if missing, like every other command would.
            var existed = File.Exists(paths.DatabasePath);
            DatabaseCheck database;
            try
            {
                var version = await services.InitializeSmartFileOrganizerDatabaseAsync(ct);
                database = new DatabaseCheck(paths.DatabasePath, existed, version, true, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                database = new DatabaseCheck(paths.DatabasePath, existed, null, false, ex.Message);
            }

            var options = services.GetRequiredService<OllamaOptions>();
            var ollama = services.GetRequiredService<IOllamaService>();

            ModelEndpointProbe probe;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(OllamaTimeout);
                try
                {
                    probe = await ollama.ProbeEndpointAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Our own limit: a cancelled token is not a refusal, the server did not answer in time.
                    probe = new ModelEndpointProbe(false, false);
                }
            }
            ct.ThrowIfCancellationRequested();

            var installed = new List<string>();
            bool? modelAvailable = null;
            if (probe.Reachable && !probe.Busy)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(OllamaTimeout);
                    installed.AddRange((await ollama.GetLocalModelsAsync(timeout.Token)).Select(m => m.Name));
                    modelAvailable = installed.Contains(options.Model, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    ctx.Logger.LogWarning("Could not list Ollama models: {Error}", ex.Message);
                }
            }

            // Library roots (https://github.com/crack666/smart_file_organizer/issues/85): a share that is not connected makes
            // every target there unverifiable; library match and findings targets refuse to run then.
            var libraries = new List<LibraryCheck>();
            if (database.Ok)
            {
                var roots = services.GetRequiredService<ILibraryRootProbe>();
                foreach (var job in (await services.GetRequiredService<IScanJobRepository>().GetAllAsync(ct))
                             .Where(j => j.Role == ScanRole.Library && j.Status == JobStatus.Completed).OrderBy(j => j.Id))
                {
                    var problem = roots.Problem(job.RootPath);
                    libraries.Add(new LibraryCheck(job.Id, job.RootPath, problem is null, problem));
                }
            }
            var librariesOk = libraries.All(l => l.Readable);
            var requested = Math.Clamp(options.MaxParallelRequests, 1, 8);
            int? measured = null;
            if (probe.Reachable && !probe.Busy && modelAvailable == true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    measured = await ollama.MeasureParallelismAsync(requested, timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    measured = null;
                }
            }
            var warning = ModelWire.ParallelWarning(requested, measured);
            if (warning is not null)
                ctx.Note(warning);

            var result = new DoctorResult(
                database.Ok && probe.Reachable && !probe.Busy && modelAvailable == true && librariesOk,
                database,
                paths.SettingsPath,
                new OllamaCheck(options.BaseUrl, probe.Reachable),
                new ModelCheck(options.Model, modelAvailable, installed),
                libraries,
                options.Provider,
                options.ResolveApiKey() is not null,
                probe.Busy,
                requested,
                measured,
                warning);

            if (!database.Ok)
                return CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.DatabaseError, $"Database check failed: {database.Error}", result, () => Render(result));
            if (!probe.Reachable)
                return CommandOutcome.Fail(ExitCodes.OllamaUnavailable, ErrorCodes.OllamaUnavailable, $"Ollama is not reachable at {options.BaseUrl}.", result, () => Render(result));
            if (probe.Busy)
                return CommandOutcome.Fail(ExitCodes.OllamaUnavailable, ErrorCodes.OllamaUnavailable,
                    $"The model server at {options.BaseUrl} is refusing requests (HTTP 429 or 503). It is reachable; try again when it is free.",
                    result, () => Render(result));
            if (modelAvailable != true)
                return CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.ModelMissing, $"Model {options.Model} is not installed in Ollama (ollama pull {options.Model}).", result, () => Render(result));
            if (!librariesOk)
                return CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.LibraryUnreachable,
                    $"Cannot read {string.Join("; ", libraries.Where(l => !l.Readable).Select(l => $"library {l.JobId} ({l.RootPath}): {l.Problem}"))}.",
                    result, () => Render(result));
            return CommandOutcome.Success(result, () => Render(result));
        });

        return command;
    }

    private static string Render(DoctorResult r)
    {
        static string Mark(bool? ok) => ok switch { true => "ok  ", false => "FAIL", null => "?   " };

        var sb = new StringBuilder();
        sb.AppendLine($"{Mark(r.Database.Ok)} database  {r.Database.Path} (schema {r.Database.SchemaVersion?.ToString() ?? "-"}){(r.Database.Error is { } e ? $": {e}" : string.Empty)}");
        sb.AppendLine($"     settings  {r.SettingsPath}");
        sb.AppendLine($"{Mark(r.Ollama.Reachable)} {r.Provider,-10} {r.Ollama.BaseUrl}{(r.Busy ? " (refusing requests)" : string.Empty)}{(r.ApiKeyConfigured ? " (key set)" : string.Empty)}");
        sb.Append($"{Mark(r.Model.Available)} model     {r.Model.Name}");
        if (r.ParallelMeasured is int measured)
            sb.AppendLine().Append($"     parallel  requested {r.ParallelRequested}, server answered about {measured} at once");
        if (r.ParallelWarning is { } warning)
            sb.AppendLine().Append($"     warning   {warning}");
        foreach (var l in r.Libraries ?? [])
            sb.AppendLine().Append($"{Mark(l.Readable)} library   {l.JobId} {l.RootPath}{(l.Problem is { } p ? $": {p}" : string.Empty)}");
        return sb.ToString();
    }
}
