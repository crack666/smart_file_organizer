using System.Text.Json;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Exchange;

/// <summary>
/// <c>sfo import-results &lt;job&gt; &lt;files.jsonl&gt;</c>: judgements made by another program, in the line format of
/// <c>files.jsonl</c> (https://github.com/crack666/smart_file_organizer/issues/34). sfo then plans and moves as if its
/// own model had judged.
/// </summary>
/// <remarks>
/// <para>Every line is checked against <c>file.schema.json</c> before anything is written; one invalid line and the
/// import writes nothing. A line is matched to a file of the job by <c>relative_path</c> (exactly, else ignoring case
/// when that is unambiguous). Lines without <c>result</c> and lines whose path is not in the job are counted, not
/// fatal.</para>
/// <para>A matched file gets the result with origin <c>external</c>, replacing a result it had. A correction of a person
/// (<c>user_overrides</c>) is not touched and still wins. The markers of the line are stored as markers of the result,
/// so a file another program calls sensitive is protected like one the model marked; the rules of sfo keep marking on
/// their own.</para>
/// </remarks>
public sealed class ExchangeImportService
{
    /// <summary>At most this many paths are listed per kind of problem.</summary>
    public const int ListedPaths = 20;

    private readonly IScanJobRepository _jobs;
    private readonly IJobReadRepository _read;
    private readonly IClassificationRepository _results;
    private readonly IFileRepository _files;

    public ExchangeImportService(IScanJobRepository jobs, IJobReadRepository read, IClassificationRepository results, IFileRepository files)
    {
        _jobs = jobs;
        _read = read;
        _results = results;
        _files = files;
    }

    /// <summary>What an import did.</summary>
    public sealed record ImportSummary(
        long JobId,
        int Lines,
        int Imported,
        int WithoutResult,
        int Unmatched,
        IReadOnlyList<string> UnmatchedPaths);

    /// <summary>A file of lines that does not match the schema; nothing was written.</summary>
    public sealed class InvalidImportException(IReadOnlyList<string> problems)
        : Exception($"{problems.Count} problem(s) in the import file; nothing was imported. First: {problems[0]}")
    {
        public IReadOnlyList<string> Problems { get; } = problems;
    }

    public async Task<ImportSummary> ImportAsync(long jobId, TextReader lines, CancellationToken ct = default)
    {
        _ = await _jobs.GetByIdAsync(jobId, ct)
            ?? throw new JobReadException(ReadFailure.JobNotFound, $"Job {jobId} not found.");

        // Read and check everything first: a half-applied import is worse than none.
        var parsed = new List<(int Line, ExchangeFile File)>();
        var problems = new List<string>();
        var number = 0;
        while (await lines.ReadLineAsync(ct) is { } text)
        {
            number++;
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (number == 1 && text[0] == '﻿') text = text[1..];

            JsonElement element;
            try
            {
                using var doc = JsonDocument.Parse(text);
                element = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                problems.Add($"line {number}: not JSON ({ex.Message})");
                continue;
            }

            var errors = ExchangeSchemas.Validate(ExchangeSchemas.FileSchema, element);
            if (errors.Count > 0)
            {
                problems.AddRange(errors.Select(e => $"line {number}: {e}"));
                continue;
            }
            parsed.Add((number, element.Deserialize<ExchangeFile>(ExchangeFormat.JsonOptions)!));
        }

        if (problems.Count > 0)
            throw new InvalidImportException(problems);

        var exact = new Dictionary<string, FileNode>(StringComparer.Ordinal);
        var folded = new Dictionary<string, List<FileNode>>(StringComparer.OrdinalIgnoreCase);
        await foreach (var row in _read.StreamFilesAsync(new FileListFilter { JobId = jobId }, ct))
        {
            var key = ExchangeFormat.PortablePath(row.File.RelativePath);
            exact[key] = row.File;
            if (!folded.TryGetValue(key, out var list)) folded[key] = list = [];
            list.Add(row.File);
        }

        int imported = 0, withoutResult = 0, unmatched = 0;
        var unmatchedPaths = new List<string>();
        var now = DateTime.UtcNow;
        foreach (var (_, line) in parsed)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Result is null)
            {
                withoutResult++;
                continue;
            }

            var path = ExchangeFormat.PortablePath(line.RelativePath);
            var file = exact.GetValueOrDefault(path)
                       ?? (folded.TryGetValue(path, out var candidates) && candidates.Count == 1 ? candidates[0] : null);
            if (file is null)
            {
                unmatched++;
                if (unmatchedPaths.Count < ListedPaths) unmatchedPaths.Add(path);
                continue;
            }

            await _results.UpsertAsync(ToClassification(file.Id, line, now), ct);
            await _files.UpdateStatusAsync(file.Id, FileNodeStatus.AiAnalyzed, ct);
            imported++;
        }

        return new ImportSummary(jobId, parsed.Count, imported, withoutResult, unmatched, unmatchedPaths);
    }

    /// <summary>The stored result for one line that passed the schema.</summary>
    public static FileClassification ToClassification(long fileNodeId, ExchangeFile line, DateTime importedAt)
    {
        var r = line.Result ?? throw new ArgumentException("The line has no result.", nameof(line));
        var markers = SensitiveMarker.None;
        foreach (var token in line.Sensitive)
        {
            if (SensitiveMarkerNames.TryParseToken(token, out var marker))
                markers |= marker;
        }

        return new FileClassification
        {
            FileNodeId = fileNodeId,
            Category = ExchangeFormat.TryParseSnake<FileCategory>(r.Category, out var category) ? category : FileCategory.Unknown,
            Importance = ExchangeFormat.TryParseSnake<Importance>(r.Importance, out var importance) ? importance : Importance.Unknown,
            Confidence = r.Confidence ?? 0,
            Certainty = VerdictCodes.ParseNullable<ResultCertainty>(r.Certainty),
            Summary = r.Summary,
            KeepHint = VerdictCodes.ParseNullable<KeepHint>(r.KeepHint),
            KeepReason = r.KeepReason,
            ModelUsed = r.Model,
            AnalyzedAt = r.AnalyzedAt ?? importedAt,
            SensitiveMarkers = markers,
            Origin = ResultOrigin.External,
            IsAiResult = false
        };
    }
}
