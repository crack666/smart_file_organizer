using System.Text;
using System.Text.Json;
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

    public ExchangeExportService(IScanJobRepository jobs, IJobReadRepository read, JobReadService readService,
        IDirectoryVerdictRepository verdicts, IFileFactRepository facts, IContentComparisonRepository comparisons)
    {
        _jobs = jobs;
        _read = read;
        _readService = readService;
        _verdicts = verdicts;
        _facts = facts;
        _comparisons = comparisons;
    }

    /// <summary>What an export wrote.</summary>
    public sealed record ExportSummary(string Folder, int Directories, int Files, IReadOnlyList<string> Written);

    /// <summary>
    /// Writes <c>job.json</c>, <c>directories.jsonl</c> and <c>files.jsonl</c> of <paramref name="jobId"/> into
    /// <paramref name="folder"/> (which must exist). Existing files of the same names are replaced.
    /// </summary>
    public async Task<ExportSummary> ExportAsync(long jobId, string folder, SensitiveOutputOptions output, CancellationToken ct = default)
    {
        var job = await _jobs.GetByIdAsync(jobId, ct)
            ?? throw new JobReadException(ReadFailure.JobNotFound, $"Job {jobId} not found.");

        var directories = await BuildDirectoriesAsync(job, output, ct);
        var dirIds = directories.Rows.ToDictionary(r => r.FullPath, r => r.Id, StringComparer.Ordinal);

        var dirCount = await WriteLinesAsync(Path.Combine(folder, ExchangeFormat.DirectoriesFile), directories.Lines, ct);
        var fileCount = await WriteLinesAsync(Path.Combine(folder, ExchangeFormat.FilesFile), FilesAsync(job, dirIds, output, ct), ct);

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
            Files = fileCount
        };
        // job.json last: a reader that finds it knows the line files are complete.
        await File.WriteAllTextAsync(Path.Combine(folder, ExchangeFormat.JobFile),
            JsonSerializer.Serialize(head, ExchangeFormat.IndentedOptions) + "\n", new UTF8Encoding(false), ct);

        return new ExportSummary(folder, dirCount, fileCount,
            [ExchangeFormat.JobFile, ExchangeFormat.DirectoriesFile, ExchangeFormat.FilesFile]);
    }

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
            var category = Enum.TryParse<FileCategory>(record.Category, out var c) ? c : FileCategory.Unknown;
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
