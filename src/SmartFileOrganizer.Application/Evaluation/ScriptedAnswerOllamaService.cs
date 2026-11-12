using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Evaluation;

/// <summary>
/// Stand-in for Ollama that answers from an answer file of the same shape as a truth file
/// (<c>sfo eval --stub</c>). The truth file itself is a perfect answer sheet; a copy with changed
/// entries gives deliberately wrong answers. Used to check the evaluation in CI without a model.
/// Never touches the network.
/// </summary>
/// <remarks>
/// Answers are looked up by the path relative to the scan root. A directory without an answer
/// gets a model error; a file without an answer as well, so gaps in the answer file show up as
/// <see cref="EvalOutcome.Missing"/> instead of silently passing. A file whose truth leaves the
/// category open gets the neutral answer <see cref="FileCategory.Document"/>.
/// </remarks>
public sealed class ScriptedAnswerOllamaService : IOllamaService
{
    private readonly EvalTruth _answers;

    public ScriptedAnswerOllamaService(EvalTruth answers, string name)
    {
        _answers = answers;
        ModelName = "stub:" + name;
    }

    public string ModelName { get; }

    public Task<DirectoryPreAssessmentOutcome> PreAssessDirectoryAsync(DirectoryPreAssessmentInput input, CancellationToken ct = default)
    {
        var path = EvalTruth.NormalizePath(input.Directory.RelativePath);
        var verdict = _answers.Directories.TryGetValue(path, out var answer) ? answer.Verdict : null;
        var error = verdict is null ? $"no scripted verdict for '{path}'" : null;
        var now = DateTime.UtcNow;

        var assessment = new DirectoryClassificationResult
        {
            DirectoryNodeId = input.Directory.Id,
            Phase = "pre_assessment",
            SamplingStrategy = "analyze_all",
            SampleSize = input.DirectFiles.Count,
            Homogeneity = "medium",
            ModelUsed = ModelName,
            AnalyzedAt = now,
            Error = error
        };
        var result = new DirectoryVerdict
        {
            DirectoryNodeId = input.Directory.Id,
            Verdict = verdict,
            Source = VerdictSource.Modell,
            Purpose = verdict is null ? null : "scripted answer",
            Retention = verdict is null ? null : RetentionValue.Pruefen,
            Reason = verdict is null ? null : $"scripted answer from {ModelName}",
            ModelUsed = ModelName,
            JudgedAt = now,
            Error = error
        };
        return Task.FromResult(new DirectoryPreAssessmentOutcome(assessment, result));
    }

    public Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default)
    {
        var path = EvalTruth.NormalizePath(input.File.RelativePath);
        var result = new FileClassification
        {
            FileNodeId = input.File.Id,
            ModelUsed = ModelName,
            AnalyzedAt = DateTime.UtcNow,
            IsAiResult = true,
            Importance = Importance.Medium,
            Confidence = 1.0
        };
        if (_answers.Files.TryGetValue(path, out var answer))
        {
            // The truth leaves a category open where it is ambiguous (not scored). The stub still has to commit
            // to one, like a model would; unknown would make every sample of such a folder unusable.
            result.Category = answer.Category ?? FileCategory.Document;
            result.SensitiveMarkers = answer.Sensitive;
            result.Summary = "scripted answer";
        }
        else
        {
            result.Error = $"no scripted answer for '{path}'";
        }
        return Task.FromResult(result);
    }

    public Task<DirectoryClassificationResult> SummarizeDirectoryAsync(DirectorySummaryInput input, CancellationToken ct = default) =>
        Task.FromResult(new DirectoryClassificationResult
        {
            DirectoryNodeId = input.Directory.Id,
            Phase = "summary",
            Summary = "scripted summary",
            ModelUsed = ModelName,
            AnalyzedAt = DateTime.UtcNow
        });

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OllamaLocalModel>>([]);

    public Task WarmModelAsync(string? model = null, CancellationToken ct = default) => Task.CompletedTask;
}
