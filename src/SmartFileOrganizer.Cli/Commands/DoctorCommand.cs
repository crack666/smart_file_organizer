using System.CommandLine;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Cli.Contract;
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

    public sealed record DoctorResult(bool Ok, DatabaseCheck Database, string SettingsPath, OllamaCheck Ollama, ModelCheck Model);

    public static Command Create(CliHost host, GlobalOptions globals)
    {
        var command = new Command("doctor", "Check the database, Ollama reachability and the configured model.");

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
                reachable = await ollama.IsAvailableAsync(timeout.Token);
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

            var result = new DoctorResult(
                database.Ok && reachable && modelAvailable == true,
                database,
                paths.SettingsPath,
                new OllamaCheck(options.BaseUrl, reachable),
                new ModelCheck(options.Model, modelAvailable, installed));

            if (!database.Ok)
                return CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.DatabaseError, $"Database check failed: {database.Error}", result, () => Render(result));
            if (!reachable)
                return CommandOutcome.Fail(ExitCodes.OllamaUnavailable, ErrorCodes.OllamaUnavailable, $"Ollama is not reachable at {options.BaseUrl}.", result, () => Render(result));
            if (modelAvailable != true)
                return CommandOutcome.Fail(ExitCodes.Failure, ErrorCodes.ModelMissing, $"Model {options.Model} is not installed in Ollama (ollama pull {options.Model}).", result, () => Render(result));
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
        return sb.ToString();
    }
}
