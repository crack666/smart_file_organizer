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

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OllamaLocalModel>>([]);

    public Task WarmModelAsync(string? model = null, CancellationToken ct = default) => Task.CompletedTask;

    public Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default)
    {
        FileInputs.Add(input);
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

    public Task<DirectoryClassificationResult> PreAssessDirectoryAsync(
        DirectoryPreAssessmentInput input, CancellationToken ct = default)
    {
        PreAssessedDirectories.Add(input.Directory.FullPath);
        return Task.FromResult(new DirectoryClassificationResult
        {
            Phase = "pre_assessment",
            SamplingStrategy = "analyze_all",
            SampleSize = input.DirectFiles.Count,
            Homogeneity = "high",
            ModelUsed = "fake-model",
            AnalyzedAt = DateTime.UtcNow
        });
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
