using System.Collections.Concurrent;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Hosting.Tests;

/// <summary>Deterministic stand-in for Ollama. Records what the pipeline sends; never touches the network.</summary>
internal sealed class FakeOllamaService : IOllamaService
{
    public ConcurrentBag<OllamaClassificationInput> FileInputs { get; } = [];
    public ConcurrentBag<string> PreAssessedDirectories { get; } = [];
    public ConcurrentBag<string> SummarizedDirectories { get; } = [];

    /// <summary>Called before each file classification, for example to cancel a run midway.</summary>
    public Action<OllamaClassificationInput>? BeforeClassify { get; set; }

    /// <summary>Returns an error message to fail the classification of a file with, or null to answer normally.</summary>
    public Func<OllamaClassificationInput, string?>? FailWith { get; set; }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OllamaLocalModel>>([]);

    public Task WarmModelAsync(string? model = null, CancellationToken ct = default) => Task.CompletedTask;

    public Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default)
    {
        BeforeClassify?.Invoke(input);
        ct.ThrowIfCancellationRequested();
        FileInputs.Add(input);
        if (FailWith?.Invoke(input) is { } error)
            return Task.FromResult(new FileClassification { Error = error, ModelUsed = "fake-model", AnalyzedAt = DateTime.UtcNow, IsAiResult = true });
        return Task.FromResult(new FileClassification
        {
            Category = input.File.FileType == FileType.Image ? FileCategory.Photo : FileCategory.Document,
            Importance = Importance.Medium,
            Confidence = 0.9,
            Summary = $"fake summary of {input.File.Name}",
            ModelUsed = "fake-model",
            AnalyzedAt = DateTime.UtcNow,
            IsAiResult = true
        });
    }

    public Task<DirectoryPreAssessmentOutcome> PreAssessDirectoryAsync(
        DirectoryPreAssessmentInput input, CancellationToken ct = default)
    {
        PreAssessedDirectories.Add(input.Directory.FullPath);
        var assessment = new DirectoryClassificationResult
        {
            Phase = "pre_assessment",
            SamplingStrategy = "analyze_all",
            SampleSize = input.DirectFiles.Count,
            Homogeneity = "high",
            ModelUsed = "fake-model",
            AnalyzedAt = DateTime.UtcNow
        };
        var verdict = new DirectoryVerdict
        {
            Verdict = input.Profile.Suggests ?? DirectoryVerdictKind.Gemischt,
            Source = VerdictSource.Modell,
            Purpose = $"fake purpose of {input.Directory.Name}",
            Retention = RetentionValue.Pruefen,
            Reason = "fake reason",
            ModelUsed = "fake-model",
            JudgedAt = DateTime.UtcNow
        };
        return Task.FromResult(new DirectoryPreAssessmentOutcome(assessment, verdict));
    }

    public Task<DirectoryClassificationResult> SummarizeDirectoryAsync(
        DirectorySummaryInput input, CancellationToken ct = default)
    {
        SummarizedDirectories.Add(input.Directory.FullPath);
        return Task.FromResult(new DirectoryClassificationResult
        {
            Phase = "summary",
            Summary = $"fake directory summary ({input.FileSummaryLines.Count} files)",
            ModelUsed = "fake-model",
            AnalyzedAt = DateTime.UtcNow
        });
    }
}
