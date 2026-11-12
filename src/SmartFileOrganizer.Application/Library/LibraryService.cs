using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Library;

/// <summary>Why <see cref="LibraryService"/> could not answer.</summary>
public enum LibraryFailure
{
    JobNotFound,
    ScanNotCompleted,

    /// <summary><c>library show</c> got a source job, or <c>library match</c> a library job where a source belongs.</summary>
    WrongRole,

    /// <summary>The path is neither a folder nor a file of the job.</summary>
    PathNotFound,

    BundleNotFound,

    /// <summary>The patterns file next to the database cannot be read; the message lists every problem.</summary>
    PatternsInvalid,

    /// <summary>A threshold of section <c>LibraryMatch</c> is out of range.</summary>
    OptionsInvalid
}

public sealed class LibraryServiceException(LibraryFailure failure, string message) : Exception(message)
{
    public LibraryFailure Failure { get; } = failure;
}

/// <summary>What <c>sfo library review</c> asks for.</summary>
public sealed record LibraryReviewQuery
{
    public long JobId { get; init; }
    public LibraryWords Words { get; init; } = LibraryWords.Default;
    public LibraryReviewOptions Options { get; init; } = new();
    public IReadOnlyCollection<LibraryFindingKind> Kinds { get; init; } = [];
    public int Limit { get; init; } = 50;
    public int Offset { get; init; }

    /// <summary>Ask the local model for roles, what is good, changes and an outline.</summary>
    public bool UseModel { get; init; } = true;

    /// <summary>Ask again even if an answer to the same facts is stored.</summary>
    public bool Refresh { get; init; }

    /// <summary>Model for this run; null for the configured one.</summary>
    public string? Model { get; init; }
}

/// <summary>Counts of a job's tree, with and without dumps.</summary>
public sealed record LibraryStructure(
    int Directories,
    long Files,
    long Bytes,
    long SystemFiles,
    int MaxDepth,
    int MedianDepth,
    bool Complete,
    int Dumps,
    int DumpDirectories,
    long DumpFiles,
    long DumpBytes);

/// <summary>The answer of <c>sfo library review</c>: one page of findings plus counts and the model's advice.</summary>
public sealed record LibraryReview(
    ScanJob Job,
    LibraryStructure Structure,
    IReadOnlyDictionary<string, int> ByKind,
    int ExcludedByExceptions,
    int Total,
    int Limit,
    int Offset,
    int? NextOffset,
    IReadOnlyList<LibraryFinding> Items,
    LibraryAdvice Advice,
    string WordsSource);

/// <summary>What <c>sfo library propose</c> asks for.</summary>
public sealed record LibraryProposeQuery
{
    public long JobId { get; init; }
    public LibraryWords Words { get; init; } = LibraryWords.Default;
    public bool UseModel { get; init; } = true;
    public bool Refresh { get; init; }
    public string? Model { get; init; }

    /// <summary>Root written into the target file; null leaves it for the person.</summary>
    public string? Root { get; init; }

    /// <summary>Language of folder names in the rule fallback: <c>en</c> or <c>de</c>.</summary>
    public string Language { get; init; } = "en";
}

/// <summary>The answer of <c>sfo library propose</c>: a target structure and how it was made.</summary>
/// <param name="Status">An <see cref="AdviceStatus"/> code for the model part; the rule fallback is used unless it is ok or cached.</param>
public sealed record LibraryProposal(ScanJob Job, TargetStructure Target, string Status, string? Error, int Dropped);

/// <summary>
/// <c>sfo library review</c> and <c>sfo library propose</c>: judge a tree by its structure only and suggest
/// improvements or a target structure. Reads scan data; writes nothing but the model's stored answer.
/// Nothing here changes a file of the judged tree.
/// </summary>
public sealed class LibraryService
{
    public const string ReviewTask = "review";
    public const string ProposeTask = "propose";

    private readonly IScanJobRepository _jobs;
    private readonly IJobReadRepository _reads;
    private readonly ILibraryRepository _library;
    private readonly ILibraryAdvisor _advisor;
    private readonly IOllamaService _ollama;
    private readonly ILogger<LibraryService> _logger;

    public LibraryService(
        IScanJobRepository jobs,
        IJobReadRepository reads,
        ILibraryRepository library,
        ILibraryAdvisor advisor,
        IOllamaService ollama,
        ILogger<LibraryService> logger)
    {
        _jobs = jobs;
        _reads = reads;
        _library = library;
        _advisor = advisor;
        _ollama = ollama;
        _logger = logger;
    }

    public async Task<LibraryReview> ReviewAsync(LibraryReviewQuery query, CancellationToken ct = default)
    {
        var (job, tree) = await LoadAsync(query.JobId, query.Words, ct);
        var rules = LibraryRules.Run(tree, query.Words, query.Options);

        var filtered = query.Kinds.Count == 0 ? rules.Findings : rules.Findings.Where(f => query.Kinds.Contains(f.Kind)).ToList();
        var page = filtered.Skip(query.Offset).Take(query.Limit).ToList();
        int? next = query.Offset + page.Count < filtered.Count ? query.Offset + page.Count : null;
        var byKind = rules.Findings.GroupBy(f => f.Kind)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .ToDictionary(g => g.Key.ToCode(), g => g.Count());

        LibraryAdvice advice;
        if (!query.UseModel)
        {
            advice = LibraryAdvice.Without(AdviceStatus.Skipped);
        }
        else
        {
            var facts = LibraryFacts.ForReview(tree, rules);
            var known = tree.All.Where(n => !n.InDump || n.Dump == n).Select(n => n.RelativePath).ToHashSet(StringComparer.Ordinal);
            var (status, record) = await AskAsync(job.Id, ReviewTask, LibraryAdviceTask.Review, facts, query.Model, query.Refresh, ct);
            advice = record is null
                ? LibraryAdvice.Without(status)
                : record.Error is not null
                    ? LibraryAdvice.Without(AdviceStatus.Failed, record.Error, record.Model)
                    : LibraryAdviceParser.Parse(record.Content, known, status, record.Model, record.CreatedAt);
        }

        return new LibraryReview(job, Structure(tree, rules), byKind, rules.ExcludedByExceptions,
            filtered.Count, query.Limit, query.Offset, next, page, advice, query.Words.Source);
    }

    public async Task<LibraryProposal> ProposeAsync(LibraryProposeQuery query, CancellationToken ct = default)
    {
        var (job, tree) = await LoadAsync(query.JobId, query.Words, ct);
        var rules = LibraryRules.Run(tree, query.Words, new LibraryReviewOptions());
        var fallback = TargetProposer.FromRules(job, tree, query.Root, query.Language);

        if (!query.UseModel)
            return new LibraryProposal(job, fallback, AdviceStatus.Skipped, null, 0);

        var facts = LibraryFacts.ForProposal(tree, rules);
        var (status, record) = await AskAsync(job.Id, ProposeTask, LibraryAdviceTask.Propose, facts, query.Model, query.Refresh, ct);
        if (record is null)
            return new LibraryProposal(job, fallback, status, null, 0);
        if (record.Error is not null)
            return new LibraryProposal(job, fallback, AdviceStatus.Failed, record.Error, 0);

        var (target, dropped, error) = TargetProposer.FromModel(job, record.Content, query.Root, record.Model, record.CreatedAt);
        return target is null
            ? new LibraryProposal(job, fallback, AdviceStatus.Failed, error, dropped)
            : new LibraryProposal(job, target, status, null, dropped);
    }

    internal async Task<(ScanJob Job, LibraryTree Tree)> LoadAsync(long jobId, LibraryWords words, CancellationToken ct)
    {
        var job = await _jobs.GetByIdAsync(jobId, ct)
                  ?? throw new LibraryServiceException(LibraryFailure.JobNotFound, $"Job {jobId} not found.");
        if (job.Status != JobStatus.Completed)
            throw new LibraryServiceException(LibraryFailure.ScanNotCompleted,
                $"The scan of job {jobId} is {job.Status.ToString().ToLowerInvariant()}; finish it first (sfo scan --resume {jobId}).");

        var dirs = await _reads.GetDirectoriesAsync(jobId, ct);
        if (dirs.Count == 0)
            throw new LibraryServiceException(LibraryFailure.ScanNotCompleted, $"Job {jobId} has no directories recorded.");
        var tree = await LibraryTree.BuildAsync(job.RootPath, dirs, _library.StreamStructureFilesAsync(jobId, ct), words, ct);
        return (job, tree);
    }

    /// <summary>
    /// The stored answer to the same facts, or a new one. Returns the status and the record, or a null record
    /// when Ollama is not reachable.
    /// </summary>
    internal async Task<(string Status, LibraryAdviceRecord? Record)> AskAsync(
        long jobId, string task, LibraryAdviceTask kind, string facts, string? model, bool refresh, CancellationToken ct)
    {
        var hash = Hash(task + "\n" + (model ?? string.Empty) + "\n" + facts);
        if (!refresh && await _library.GetAdviceAsync(jobId, task, hash, ct) is { Error: null } stored)
            return (AdviceStatus.Cached, stored);

        if (!await _ollama.IsAvailableAsync(ct))
            return (AdviceStatus.Unavailable, null);

        _logger.LogInformation("Asking the local model for library {Task} of job {JobId} ({Chars} characters of facts)", task, jobId, facts.Length);
        var reply = await _advisor.AskAsync(new LibraryAdviceRequest(kind, facts, model, null), ct);
        var record = new LibraryAdviceRecord
        {
            JobId = jobId,
            Task = task,
            InputHash = hash,
            Model = reply.Model,
            CreatedAt = DateTime.UtcNow,
            Content = reply.Content,
            RawResponse = reply.RawResponse,
            Error = reply.Error
        };
        await _library.SaveAdviceAsync(record, ct);
        return (AdviceStatus.Ok, record);
    }

    private static LibraryStructure Structure(LibraryTree tree, LibraryRuleResult rules)
    {
        var depths = tree.All.Select(n => n.Depth).OrderBy(d => d).ToList();
        var root = tree.Root;
        return new LibraryStructure(
            tree.All.Count, root.TreeFiles, root.TreeBytes, tree.SystemFiles,
            depths.Count == 0 ? 0 : depths[^1], depths.Count == 0 ? 0 : depths[depths.Count / 2],
            root.TreeComplete,
            rules.Dumps.Count, rules.Dumps.Sum(d => d.TreeDirs + 1), rules.Dumps.Sum(d => d.TreeFiles), rules.Dumps.Sum(d => d.TreeBytes));
    }

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
