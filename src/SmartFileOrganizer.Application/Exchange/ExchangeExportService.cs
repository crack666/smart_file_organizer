using System.Text;
using System.Text.Json;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Exchange;

/// <summary>
/// <c>sfo export &lt;job&gt; --format jsonl --out &lt;folder&gt;</c>: a job in the open exchange format
/// (<see cref="ExchangeFormat"/>, https://github.com/crack666/smart_file_organizer/issues/34).
/// </summary>
/// <remarks>
/// Read-only like the CSV export. Every file passes <see cref="SensitiveOutputGuard.Project"/> and every directory the
/// directory listing of <see cref="JobReadService"/>, so the export shows exactly what <c>sfo files</c> and
/// <c>sfo dirs</c> show with the same options: a protected file carries no model summary, raw response, error text or
/// free-text fact.
/// </remarks>
public sealed class ExchangeExportService
{
    private readonly IScanJobRepository _jobs;
    private readonly IJobReadRepository _read;
    private readonly JobReadService _readService;
    private readonly IDirectoryVerdictRepository _verdicts;
    private readonly IFileFactRepository _facts;
    private readonly IContentComparisonRepository _comparisons;
    private readonly FindingService _findings;
    private readonly IPlanRepository _plans;

    public ExchangeExportService(IScanJobRepository jobs, IJobReadRepository read, JobReadService readService,
        IDirectoryVerdictRepository verdicts, IFileFactRepository facts, IContentComparisonRepository comparisons,
        FindingService findings, IPlanRepository plans)
    {
        _jobs = jobs;
        _read = read;
        _readService = readService;
        _verdicts = verdicts;
        _facts = facts;
        _comparisons = comparisons;
        _findings = findings;
        _plans = plans;
    }

    /// <summary>What an export wrote. <paramref name="Findings"/> and <paramref name="PlanActions"/> are null for a library job.</summary>
    public sealed record ExportSummary(string Folder, int Directories, int Files, IReadOnlyList<string> Written,
        int? Findings = null, int? PlanActions = null);

    /// <summary>
    /// Writes <c>job.json</c>, <c>directories.jsonl</c> and <c>files.jsonl</c> of <paramref name="jobId"/> into
    /// <paramref name="folder"/> (which must exist), and for a source also <c>findings.jsonl</c> and <c>plan.jsonl</c>
    /// (empty when there is none). Existing files of the same names are replaced.
    /// </summary>
    public async Task<ExportSummary> ExportAsync(long jobId, string folder, SensitiveOutputOptions output, CancellationToken ct = default)
    {
        var job = await _jobs.GetByIdAsync(jobId, ct)
            ?? throw new JobReadException(ReadFailure.JobNotFound, $"Job {jobId} not found.");

        var directories = await BuildDirectoriesAsync(job, output, ct);
        var dirIds = directories.Rows.ToDictionary(r => r.FullPath, r => r.Id, StringComparer.Ordinal);

        var dirCount = await WriteLinesAsync(Path.Combine(folder, ExchangeFormat.DirectoriesFile), directories.Lines, ct);
        var fileCount = await WriteLinesAsync(Path.Combine(folder, ExchangeFormat.FilesFile), FilesAsync(job, dirIds, output, ct), ct);

        List<string> parts = [ExchangeFormat.DirectoriesFile, ExchangeFormat.FilesFile];
        int? findingCount = null, actionCount = null;
        IReadOnlyList<ExchangePlan> plans = [];
        // Findings and plans are about sources; a library has neither (FindingService refuses it).
        if (job.Role != ScanRole.Library)
        {
            var findings = await BuildFindingsAsync(job, dirIds, ct);
            findingCount = await WriteLinesAsync(Path.Combine(folder, ExchangeFormat.FindingsFile), findings, ct);
            var (planHeads, actions) = await BuildPlanAsync(job, ct);
            actionCount = await WriteLinesAsync(Path.Combine(folder, ExchangeFormat.PlanFile), actions, ct);
            plans = planHeads;
            parts.Add(ExchangeFormat.FindingsFile);
            parts.Add(ExchangeFormat.PlanFile);
        }

        var head = new ExchangeJob
        {
            ExportedAt = DateTime.UtcNow,
            Disclosure = output.Disclosure == SensitiveDisclosure.Restricted ? SensitiveOutputOptions.RestrictedCode : SensitiveOutputOptions.OpenCode,
            IncludeSensitive = output.IncludeSensitive,
            JobId = job.Id,
            RootPath = job.RootPath,
            Role = job.Role.ToValue(),
            Status = ExchangeFormat.Snake(job.Status),
            StructureOnly = job.StructureOnly,
            RulesName = job.RulesName,
            CreatedAt = ExchangeFormat.Utc(job.CreatedAt),
            CompletedAt = ExchangeFormat.Utc(job.CompletedAt),
            Directories = dirCount,
            Files = fileCount,
            Findings = findingCount,
            PlanActions = actionCount,
            Plans = plans,
            Parts = parts
        };
        // job.json last: a reader that finds it knows the line files are complete.
        await File.WriteAllTextAsync(Path.Combine(folder, ExchangeFormat.JobFile),
            JsonSerializer.Serialize(head, ExchangeFormat.IndentedOptions) + "\n", new UTF8Encoding(false), ct);

        return new ExportSummary(folder, dirCount, fileCount, [ExchangeFormat.JobFile, .. parts], findingCount, actionCount);
    }

    /// <summary>The findings of a source in every state, in rank order, each with the ids of its files and its folder.</summary>
    public async Task<IReadOnlyList<ExchangeFinding>> BuildFindingsAsync(ScanJob job, IReadOnlyDictionary<string, long> directoryIds,
        CancellationToken ct = default)
    {
        var report = await _findings.ComputeAsync(job.Id, ct);
        return report.Findings.OrderBy(f => f.Rank).Select(f => ToLine(f, directoryIds)).ToList();
    }

    /// <summary>The line of one finding. Public for tests.</summary>
    public static ExchangeFinding ToLine(Finding f, IReadOnlyDictionary<string, long> directoryIds) => new()
    {
        Key = f.Key,
        Rank = f.Rank,
        Kind = f.Kind.ToCode(),
        Certainty = f.Certainty.ToCode(),
        Section = f.Unsure ? "unsure" : "sure",
        State = f.State.ToCode(),
        Title = f.Title,
        Reason = f.Reason,
        RelativePath = f.RelativePath is null ? null : ExchangeFormat.PortablePath(f.RelativePath),
        DirectoryId = f.Path is { } path && directoryIds.TryGetValue(path, out var dirId) ? dirId : null,
        BundleId = f.BundleId,
        BundleKind = f.BundleKind.ToCode(),
        Files = f.Files,
        Bytes = f.Bytes,
        Group = f.Group,
        ByFileType = f.ByFileType,
        Period = f.Period is { } p
            ? new ExchangePeriod { FromLocal = Local(p.From), ToLocal = Local(p.To), TimeSource = p.Source.ToCode() }
            : null,
        Comparison = f.Comparison,
        Markers = f.Markers,
        SensitiveRules = f.SensitiveRules,
        Action = new ExchangeFindingAction { Kind = f.Action.Kind.ToCode(), Reason = f.Action.Reason, SingleDecision = f.Action.SingleDecision },
        Target = f.Target is { } t
            ? new ExchangeFindingTarget
            {
                LibraryJobId = t.LibraryJobId,
                LibraryRoot = t.LibraryRoot,
                Path = t.Path,
                RelativePath = ExchangeFormat.PortablePath(t.RelativePath),
                Certainty = t.Certainty.ToCode(),
                Reason = t.Reason,
                SameContentFiles = t.SameContentFiles,
                ContentCheck = t.ContentCheck.ToCode()
            }
            : null,
        TargetStatus = f.TargetStatus.ToCode(),
        TargetDecidedBy = f.TargetDecidedBy.ToCode(),
        TargetParts = f.TargetParts.Select(part => new ExchangeTargetPart
        {
            Index = part.Index,
            Label = part.Label,
            Status = part.Status.ToCode(),
            LibraryJobId = part.LibraryJobId,
            Path = part.Path,
            RelativePath = part.RelativePath is null ? null : ExchangeFormat.PortablePath(part.RelativePath),
            Certainty = part.Certainty.ToCode(),
            ContentCheck = part.ContentCheck.ToCode(),
            Reason = part.Reason,
            DecidedBy = part.DecidedBy.ToCode(),
            FileIds = part.FileIds.Order().ToList()
        }).ToList(),
        Duplicates = f.Duplicates is { } d
            ? new ExchangeDuplicates
            {
                Groups = d.Groups,
                RedundantFiles = d.RedundantFiles,
                RedundantBytes = d.RedundantBytes,
                Directories = d.Directories.Select(ExchangeFormat.PortablePath).ToList(),
                DirectoriesTotal = d.DirectoriesTotal,
                Keep = ExchangeFormat.PortablePath(d.Keep),
                Scope = d.Scope
            }
            : null,
        Examples = f.Examples.Select(ExchangeFormat.PortablePath).ToList(),
        Notes = f.Notes,
        FileIds = f.FileIds.Order().ToList(),
        DecidedBy = f.DecidedBy.ToCode(),
        DecidedAt = ExchangeFormat.Utc(f.DecidedAt),
        DecisionNote = f.DecisionNote
    };

    /// <summary>Local wall-clock time without an offset, as <c>sfo bundles</c> and <c>sfo findings</c> write it.</summary>
    private static string? Local(DateTime? time) =>
        time?.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The plans of a source, oldest first, and their actions in plan and id order.</summary>
    public async Task<(IReadOnlyList<ExchangePlan> Plans, IReadOnlyList<ExchangePlanAction> Actions)> BuildPlanAsync(
        ScanJob job, CancellationToken ct = default)
    {
        var heads = new List<ExchangePlan>();
        var lines = new List<ExchangePlanAction>();
        foreach (var plan in (await _plans.GetByJobAsync(job.Id, ct)).OrderBy(p => p.Id))
        {
            var actions = (await _plans.GetActionsAsync(plan.Id, ct)).OrderBy(a => a.Id).ToList();
            heads.Add(new ExchangePlan
            {
                Id = plan.Id,
                CreatedAt = ExchangeFormat.Utc(plan.CreatedAt),
                LibraryJobIds = plan.Libraries,
                Notes = plan.Notes,
                Actions = actions.Count
            });
            lines.AddRange(actions.Select(ToLine));
        }
        return (heads, lines);
    }

    /// <summary>The line of one plan action. Public for tests.</summary>
    public static ExchangePlanAction ToLine(PlanAction a) => new()
    {
        PlanId = a.PlanId,
        Id = a.Id,
        FindingKey = a.FindingKey,
        FindingKind = a.FindingKind.ToCode(),
        Unit = a.Unit,
        SourceKind = a.SourceKind.ToCode(),
        RelativePath = ExchangeFormat.PortablePath(a.RelativePath),
        FileId = a.SourceKind == PlanSourceKind.File ? a.FileNodeId : null,
        DirectoryId = a.SourceKind == PlanSourceKind.Folder ? a.DirectoryNodeId : null,
        Files = a.Files,
        Bytes = a.Bytes,
        Kind = a.Kind.ToCode(),
        Recommended = a.Recommended.ToCode(),
        TargetPath = a.TargetPath,
        LibraryJobId = a.LibraryJobId,
        Reason = a.Reason,
        Note = a.Note,
        Origin = a.Origin.ToCode(),
        State = a.State.ToCode(),
        Sensitive = a.Sensitive,
        SingleDecision = a.SingleDecision,
        DecidedBy = a.DecidedBy.ToCode(),
        DecidedAt = ExchangeFormat.Utc(a.DecidedAt),
        UpdatedAt = ExchangeFormat.Utc(a.UpdatedAt)
    };

    /// <summary>One line object per directory, in path order.</summary>
    public async Task<(IReadOnlyList<DirectoryReadRow> Rows, IReadOnlyList<ExchangeDirectory> Lines)> BuildDirectoriesAsync(
        ScanJob job, SensitiveOutputOptions output, CancellationToken ct = default)
    {
        var rows = await _read.GetDirectoriesAsync(job.Id, ct);
        var byPath = rows.ToDictionary(r => r.FullPath, StringComparer.Ordinal);
        var verdicts = (await _verdicts.GetForJobAsync(job.Id, ct)).ToDictionary(v => v.DirectoryNodeId);

        // The listing of sfo dirs, page by page: it protects the model texts exactly as the read commands do.
        var items = new List<DirectoryItem>(rows.Count);
        var offset = 0;
        while (true)
        {
            var page = await _readService.ListDirectoriesAsync(new DirectoryListQuery
            {
                JobId = job.Id, Limit = JobReadService.MaxLimit, Offset = offset, Output = output, Sort = DirectorySort.Path
            }, ct);
            items.AddRange(page.Items);
            if (page.NextOffset is not { } next) break;
            offset = next;
        }

        var lines = new List<ExchangeDirectory>(items.Count);
        foreach (var item in items)
        {
            byPath.TryGetValue(item.FullPath, out var row);
            long? parentId = row is not null && byPath.TryGetValue(row.ParentPath, out var parent) && parent.Id != row.Id ? parent.Id : null;
            verdicts.TryGetValue(item.Id, out var verdict);

            lines.Add(new ExchangeDirectory
            {
                Id = item.Id,
                ParentId = parentId,
                RelativePath = ExchangeFormat.PortablePath(item.RelativePath),
                Name = row?.Name ?? Path.GetFileName(item.FullPath),
                Depth = item.Depth,
                Status = item.Status.ToCode(),
                ScanReason = item.ScanReason,
                LinkKind = item.LinkKind,
                LinkTarget = item.LinkTarget,
                ErrorCause = item.ErrorCause,
                DirectFiles = item.DirectFiles,
                Files = item.Files,
                SizeBytes = item.SizeBytes,
                Subdirs = item.Subdirs,
                Sensitive = item.Sensitive,
                SensitiveRule = item.SensitiveRule,
                SensitiveFiles = item.SensitiveFiles,
                TextWithheld = item.TextWithheld,
                Summary = item.Summary,
                Verdict = item.VerdictSource is null && item.VerdictError is null ? null : new ExchangeVerdict
                {
                    Verdict = item.Verdict,
                    Source = item.VerdictSource ?? VerdictSource.Model.ToCode(),
                    Rule = item.VerdictRule,
                    Retention = item.Retention,
                    Purpose = item.Purpose,
                    Reason = item.Reason,
                    UnitDirectoryId = verdict?.UnitDirectoryNodeId,
                    EscalatedFrom = verdict?.EscalatedFrom.ToCode(),
                    EscalationReason = verdict?.EscalationReason,
                    Model = verdict?.ModelUsed,
                    JudgedAt = verdict is null || verdict.JudgedAt == default ? null : ExchangeFormat.Utc(verdict.JudgedAt),
                    Error = item.VerdictError,
                    Profile = verdict is null ? null : JsonSerializer.SerializeToElement(verdict.Profile, ProfileJson)
                }
            });
        }
        return (rows, lines);
    }

    private static readonly JsonSerializerOptions ProfileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    /// <summary>One line object per file, in path order, each through the output guard.</summary>
    public async IAsyncEnumerable<ExchangeFile> FilesAsync(ScanJob job, IReadOnlyDictionary<string, long> directoryIds,
        SensitiveOutputOptions output, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var facts = await _facts.GetByJobAsync(job.Id, ct);
        var (comparison, _) = await LibraryComparisonSet.LoadAsync(_comparisons, job.Id, null, ct);

        await foreach (var row in _read.StreamFilesAsync(new FileListFilter { JobId = job.Id }, ct))
        {
            var file = row.File;
            facts.TryGetValue(file.Id, out var fileFacts);
            FileComparison? compared = null;
            comparison?.Rows.TryGetValue(file.Id, out compared);
            yield return ToLine(file, directoryIds, fileFacts ?? [], compared, output);
        }
    }

    /// <summary>The line of one file. Public for tests.</summary>
    public static ExchangeFile ToLine(FileNode file, IReadOnlyDictionary<string, long> directoryIds,
        IReadOnlyList<FileFact> facts, FileComparison? compared, SensitiveOutputOptions output)
    {
        var record = SensitiveOutputGuard.Project(file, output);
        var ai = file.Classification;
        var markers = SensitiveOutputGuard.GetMarkers(file);

        ExchangeResult? result = null;
        if (record.Category is not null)
        {
            var category = ExchangeFormat.TryParseSnake<FileCategory>(record.Category, out var c) ? c : FileCategory.Unknown;
            // The guard names a correction human and a review raised by a marker rule; otherwise the stored origin counts.
            var source = record.CategorySource == ResultOrigin.Model.ToCode() && ai?.Origin is { } origin
                ? origin.ToCode()
                : record.CategorySource;
            result = new ExchangeResult
            {
                Category = ExchangeFormat.Snake(category),
                CategorySource = source,
                Origin = ai is null ? null : (ai.Origin ?? ResultOrigin.Model).ToCode(),
                Importance = ai is null || record.Importance is null ? null : ExchangeFormat.Snake(ai.Importance),
                Confidence = record.Confidence,
                Certainty = record.Certainty,
                Summary = record.Summary,
                KeepHint = ai?.KeepHint.ToCode(),
                KeepReason = record.Protected ? null : ai?.KeepReason,
                Model = record.ModelUsed,
                AnalyzedAt = ExchangeFormat.Utc(record.AnalyzedAt),
                InheritedFromDirectoryId = ai?.InheritedFromDirectoryNodeId,
                Attempts = record.Attempts,
                Error = record.Error,
                RawResponse = record.RawResponse
            };
        }

        return new ExchangeFile
        {
            Id = file.Id,
            DirectoryId = directoryIds.TryGetValue(file.ParentPath, out var dirId) ? dirId : null,
            RelativePath = ExchangeFormat.PortablePath(file.RelativePath),
            Name = file.Name,
            Extension = file.Extension,
            FileType = ExchangeFormat.Snake(file.FileType),
            SizeBytes = file.Size,
            LastWriteTime = ExchangeFormat.Utc(record.LastWriteTime),
            Status = ExchangeFormat.Snake(file.Status),
            AnalysisSkip = file.AnalysisSkip,
            ScanErrorCause = file.ScanErrorCause,
            Sensitive = record.Sensitive,
            SensitiveRule = record.SensitiveRule,
            Protected = record.Protected,
            Result = result,
            // As sfo show: a protected file shows no free text and no description of its content.
            Facts = output.ShownFacts(markers, facts)
                .Where(f => !record.Protected || (!f.Key.IsFreeText() && !f.Key.IsDescription()))
                .Select(f => new ExchangeFact { Fact = f.Key.ToCode(), Value = f.Value, Source = f.Source.ToCode(), Certainty = f.Certainty.ToCode() })
                .ToList(),
            Content = compared is null ? null : new ExchangeContent
            {
                State = compared.State?.ToValue(),
                LibraryJobId = compared.State == ContentState.BackedUp ? compared.MatchJobId : null
            }
        };
    }

    private static async Task<int> WriteLinesAsync<T>(string path, IEnumerable<T> lines, CancellationToken ct)
    {
        await using var writer = OpenLineFile(path);
        var count = 0;
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteAsync(JsonSerializer.Serialize(line, ExchangeFormat.JsonOptions));
            await writer.WriteAsync('\n');
            count++;
        }
        return count;
    }

    private static async Task<int> WriteLinesAsync<T>(string path, IAsyncEnumerable<T> lines, CancellationToken ct)
    {
        await using var writer = OpenLineFile(path);
        var count = 0;
        await foreach (var line in lines.WithCancellation(ct))
        {
            await writer.WriteAsync(JsonSerializer.Serialize(line, ExchangeFormat.JsonOptions));
            await writer.WriteAsync('\n');
            count++;
        }
        return count;
    }

    /// <summary>UTF-8 without BOM: every line must parse as JSON on its own, and a BOM breaks the first.</summary>
    private static StreamWriter OpenLineFile(string path) =>
        new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
}
