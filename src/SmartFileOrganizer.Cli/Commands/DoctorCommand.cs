using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Hosting;
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
        IReadOnlyList<LibraryCheck>? Libraries = null);

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

            bool reachable;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(OllamaTimeout);
                try
                {
                    reachable = await ollama.IsAvailableAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Our own limit: IsAvailableAsync passes a cancelled token through instead of answering false.
                    reachable = false;
                }
            }
            ct.ThrowIfCancellationRequested();

            var installed = new List<string>();
            bool? modelAvailable = null;
            if (reachable)
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
                var probe = services.GetRequiredService<ILibraryRootProbe>();
                foreach (var job in (await services.GetRequiredService<IScanJobRepository>().GetAllAsync(ct))
                             .Where(j => j.Role == ScanRole.Library && j.Status == JobStatus.Completed).OrderBy(j => j.Id))
                {
                    var problem = probe.Problem(job.RootPath);
                    libraries.Add(new LibraryCheck(job.Id, job.RootPath, problem is null, problem));
                }
            }
            var librariesOk = libraries.All(l => l.Readable);

            var result = new DoctorResult(
                database.Ok && reachable && modelAvailable == true && librariesOk,
                database,
                paths.SettingsPath,
                new OllamaCheck(options.BaseUrl, reachable),
                new ModelCheck(options.Model, modelAvailable, installed),
                libraries);

            if (!database.Ok)
                return CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.DatabaseError, $"Database check failed: {database.Error}", result, () => Render(result));
            if (!reachable)
                return CommandOutcome.Fail(ExitCodes.OllamaUnavailable, ErrorCodes.OllamaUnavailable, $"Ollama is not reachable at {options.BaseUrl}.", result, () => Render(result));
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
        sb.AppendLine($"{Mark(r.Ollama.Reachable)} ollama    {r.Ollama.BaseUrl}");
        sb.Append($"{Mark(r.Model.Available)} model     {r.Model.Name}");
        foreach (var l in r.Libraries ?? [])
            sb.AppendLine().Append($"{Mark(l.Readable)} library   {l.JobId} {l.RootPath}{(l.Problem is { } p ? $": {p}" : string.Empty)}");
        return sb.ToString();
    }
}
