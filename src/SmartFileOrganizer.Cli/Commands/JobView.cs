using System.CommandLine;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Cli.Commands;

/// <summary>A scan job as it appears in every JSON document (<c>scan</c>, <c>jobs</c>, <c>status</c>).</summary>
public sealed record JobView(
    long Id,
    string RootPath,
    ScanRole Role,
    JobStatus Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    long FilesTotal,
    long FilesProcessed,
    long DirectoriesTotal,
    long DirectoriesProcessed,
    int ErrorCount,
    string? LastError,
    RulesCommand.RulesView? Rules,
    bool StructureOnly)
{
    public static JobView From(ScanJob job) => new(
        job.Id, job.RootPath, job.Role, job.Status, job.CreatedAt, job.StartedAt, job.CompletedAt,
        job.TotalFiles, job.ProcessedFiles, job.TotalDirectories, job.ProcessedDirectories,
        job.ErrorCount, job.LastError,
        job.Rules is { } rules ? RulesCommand.RulesView.From(rules) : null,
        job.StructureOnly);

    public string ToLine() =>
        $"#{Id}  {Role.ToValue(),-7}  {Status.ToString().ToLowerInvariant(),-9}  {FilesProcessed} files, {DirectoriesProcessed} dirs, {ErrorCount} errors  {RootPath}" +
        (Rules is { } r ? $"  [rules: {r.Name}]" : string.Empty) +
        (StructureOnly ? "  [structure only]" : string.Empty);
}

/// <summary>Shared helpers for commands that take a job id.</summary>
public static class JobArguments
{
    public static Argument<long> Create(string description) => new("job")
    {
        Description = description,
        HelpName = "job"
    };

    /// <summary>Loads the job or fails with <see cref="ErrorCodes.JobNotFound"/> (exit code 1).</summary>
    public static async Task<ScanJob> RequireJobAsync(ScanJobService jobs, long id, CancellationToken ct) =>
        await jobs.GetJobAsync(id, ct)
        ?? throw new CliFailureException(ExitCodes.Failure, ErrorCodes.JobNotFound, $"Job {id} not found.");
}
