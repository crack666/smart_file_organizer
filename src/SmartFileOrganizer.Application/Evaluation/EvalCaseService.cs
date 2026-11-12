using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Evaluation;

/// <summary>
/// The input the model saw for a file, stored with a case so the case can be run again even if
/// the file is gone. Never stored for a sensitive file.
/// </summary>
public sealed record EvalFileEvidence(
    string Name,
    string Extension,
    FileType FileType,
    long Size,
    DateTime LastWriteTime,
    string RelativeDir,
    string RelativePath,
    string ExtractedText,
    IReadOnlyList<string> Base64Images,
    IReadOnlyList<FileFact>? Facts = null,
    IReadOnlyList<string>? Contains = null,
    IReadOnlyList<string>? Missing = null);

/// <summary>
/// Turns corrections of the human into test cases (<see cref="EvalCase"/>) and runs the stored
/// cases again against the current model and prompts.
/// </summary>
/// <remarks>
/// A correction becomes a case only when the model had answered and the human chose something
/// else; accepting the model's answer is not a case. Recording never fails the correction: a
/// problem is logged and the correction stands.
/// </remarks>
public sealed class EvalCaseService
{
    private readonly IEvalCaseRepository _cases;
    private readonly IFileRepository _files;
    private readonly IClassificationInputPreparer _preparer;
    private readonly IFileSystemAccessor _fs;
    private readonly ILogger<EvalCaseService> _logger;

    public EvalCaseService(
        IEvalCaseRepository cases,
        IFileRepository files,
        IClassificationInputPreparer preparer,
        IFileSystemAccessor fs,
        ILogger<EvalCaseService> logger)
    {
        _cases = cases;
        _files = files;
        _preparer = preparer;
        _fs = fs;
        _logger = logger;
    }

    /// <summary>
    /// Stores a case for a category the human set on a file. Returns the case, or null when there
    /// is nothing to keep (no model answer, or the human chose what the model said).
    /// </summary>
    public async Task<EvalCase?> RecordCategoryCorrectionAsync(long fileNodeId, FileCategory corrected, CancellationToken ct = default)
    {
        try
        {
            var node = await _files.GetByIdAsync(fileNodeId, ct);
            if (node?.Classification is not { IsAiResult: true, Error: null } answer || answer.Category == corrected)
                return null;

            // Same protection as every output: rule markers, model markers, and the name rules again.
            var markers = node.SensitiveMarkers | answer.SensitiveMarkers
                          | SensitiveFileRules.MatchName(node.Name, node.ParentPath).Markers;

            string? evidence = null;
            if (markers == SensitiveMarker.None)
            {
                var input = await _preparer.PrepareAsync(node, ct);
                evidence = JsonSerializer.Serialize(new EvalFileEvidence(
                    node.Name, node.Extension, node.FileType, node.Size, node.LastWriteTime,
                    node.RelativeDir, node.RelativePath, input.ExtractedText ?? string.Empty, input.Base64Images,
                    input.Facts, input.Contains, input.Missing), EvalJson.Compact);
            }

            var now = DateTime.UtcNow;
            var evalCase = new EvalCase
            {
                Kind = EvalCaseKind.FileCategory,
                Origin = EvalCaseOrigin.ReviewOverride,
                JobId = node.JobId,
                FileNodeId = node.Id,
                SubjectPath = node.FullPath,
                RelativePath = EvalTruth.NormalizePath(node.RelativePath),
                Expected = EvalCodes.Category(corrected),
                Observed = EvalCodes.Category(answer.Category),
                ObservedModel = answer.ModelUsed,
                SensitiveMarkers = markers,
                EvidenceJson = evidence,
                CreatedAt = now,
                UpdatedAt = now
            };
            await _cases.UpsertAsync(evalCase, ct);
            return evalCase;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not store the correction of file {FileNodeId} as a test case.", fileNodeId);
            return null;
        }
    }

    /// <summary>
    /// Runs every stored case of a model judgement through <paramref name="ollama"/> and scores it like a
    /// corpus item. Findings reported as wrong (<see cref="EvalCaseKind.Finding"/>) ask no model: they are
    /// rule results, counted by the measurement series, and left out here.
    /// </summary>
    /// <param name="modelName">Model name for the report (<c>decided_by</c>).</param>
    public async Task<EvalReport> RunCasesAsync(
        IOllamaService ollama,
        string modelName,
        IProgress<(int Done, int Total, string Path)>? progress = null,
        CancellationToken ct = default)
    {
        var report = new EvalReportBuilder();
        var cases = await _cases.GetAllAsync(EvalCaseKind.FileCategory, ct);
        var done = 0;
        foreach (var evalCase in cases)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done, cases.Count, evalCase.SubjectPath));
            var result = evalCase.Kind switch
            {
                EvalCaseKind.FileCategory => await RunCategoryCaseAsync(evalCase, ollama, ct),
                _ => throw new NotSupportedException($"Case kind {evalCase.Kind} has no runner.")
            };
            report.Case(result);
            if (result.Outcome == EvalOutcome.Hit)
                report.Hit(EvalKind.FileCategory);
            else
                report.Fail(new EvalFailure(EvalKind.FileCategory, CaseKey(evalCase), evalCase.SubjectPath, result.Outcome,
                    result.Expected, result.Actual, $"model {modelName}", result.Detail));
            progress?.Report((++done, cases.Count, evalCase.SubjectPath));
        }
        return report.Build([EvalKind.FileCategory], withCases: true);
    }

    public static string CaseKey(EvalCase evalCase) => $"{evalCase.Kind.ToCode()}:case:{evalCase.Id}";

    private async Task<EvalCaseResult> RunCategoryCaseAsync(EvalCase evalCase, IOllamaService ollama, CancellationToken ct)
    {
        var expectedOk = EvalCodes.TryParseCategory(evalCase.Expected, out var expected);
        EvalCaseResult Result(EvalOutcome outcome, string? actual, string? detail) => new(
            evalCase.Id, evalCase.Kind, evalCase.SubjectPath, evalCase.Expected, evalCase.Observed, actual, outcome,
            evalCase.IsSensitive, detail);

        if (!expectedOk)
            return Result(EvalOutcome.Missing, null, $"stored expected category '{evalCase.Expected}' is unknown");

        var input = await BuildInputAsync(evalCase, ct);
        if (input is null)
            return Result(EvalOutcome.Missing, null, "sensitive file: no evidence stored, and the file is no longer there");

        var answer = await ollama.ClassifyAsync(input, ct);
        if (answer.Error is not null)
            return Result(EvalOutcome.Missing, null, $"model error: {answer.Error}");

        var actual = EvalCodes.Category(answer.Category);
        return answer.Category == expected
            ? Result(EvalOutcome.Hit, actual, null)
            : Result(EvalOutcome.Wrong, actual, null);
    }

    /// <summary>
    /// The stored evidence; for a sensitive case (nothing stored) the file is read again, locally,
    /// if it still exists.
    /// </summary>
    private async Task<OllamaClassificationInput?> BuildInputAsync(EvalCase evalCase, CancellationToken ct)
    {
        if (evalCase.EvidenceJson is { } json)
        {
            var e = JsonSerializer.Deserialize<EvalFileEvidence>(json, EvalJson.Compact)!;
            return new OllamaClassificationInput
            {
                File = new FileNode
                {
                    Id = evalCase.FileNodeId ?? 0,
                    JobId = evalCase.JobId ?? 0,
                    FullPath = evalCase.SubjectPath,
                    Name = e.Name,
                    Extension = e.Extension,
                    FileType = e.FileType,
                    Size = e.Size,
                    LastWriteTime = e.LastWriteTime,
                    RelativeDir = e.RelativeDir,
                    RelativePath = e.RelativePath
                },
                ExtractedText = e.ExtractedText,
                Base64Images = e.Base64Images,
                Facts = e.Facts ?? [],
                Contains = e.Contains ?? [],
                Missing = e.Missing ?? []
            };
        }

        if (evalCase.FileNodeId is not { } id || await _files.GetByIdAsync(id, ct) is not { } node || !_fs.FileExists(node.FullPath))
            return null;
        return await _preparer.PrepareAsync(node, ct);
    }
}
