using System.Collections.Concurrent;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Stand-in for Ollama in Phase 1 tests. Records every pre-assessment input and answers with
/// <see cref="Answer"/> (default: the verdict the profile suggests, else <c>gemischt</c>). Never touches the network.
/// </summary>
internal sealed class ScriptedOllamaService : IOllamaService
{
    public ConcurrentBag<DirectoryPreAssessmentInput> PreAssessInputs { get; } = [];

    /// <summary>Verdict to answer for a directory name; return null to answer with an error.</summary>
    public Func<DirectoryPreAssessmentInput, DirectoryVerdictKind?> Answer { get; set; } =
        input => input.Profile.Suggests ?? DirectoryVerdictKind.Gemischt;

    public IReadOnlyList<string> CalledDirectoryNames => PreAssessInputs.Select(i => i.Directory.Name).OrderBy(n => n).ToList();

    public Task<DirectoryPreAssessmentOutcome> PreAssessDirectoryAsync(DirectoryPreAssessmentInput input, CancellationToken ct = default)
    {
        PreAssessInputs.Add(input);
        var kind = Answer(input);
        var assessment = new DirectoryClassificationResult
        {
            Phase = "pre_assessment",
            SamplingStrategy = "analyze_all",
            SampleSize = input.DirectFiles.Count,
            Homogeneity = "high",
            ModelUsed = "scripted",
            AnalyzedAt = DateTime.UtcNow,
            Error = kind is null ? "scripted failure" : null
        };
        var verdict = new DirectoryVerdict
        {
            Verdict = kind,
            Source = VerdictSource.Modell,
            Purpose = kind is null ? null : $"purpose of {input.Directory.Name}",
            Retention = kind is null ? null : RetentionValue.Behalten,
            Reason = kind is null ? null : "scripted",
            ModelUsed = "scripted",
            JudgedAt = DateTime.UtcNow,
            Error = kind is null ? "scripted failure" : null
        };
        return Task.FromResult(new DirectoryPreAssessmentOutcome(assessment, verdict));
    }

    public Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default) =>
        throw new NotSupportedException("Phase 2 is not part of these tests.");

    public Task<DirectoryClassificationResult> SummarizeDirectoryAsync(DirectorySummaryInput input, CancellationToken ct = default) =>
        throw new NotSupportedException("Phase 3 is not part of these tests.");

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OllamaLocalModel>>([]);

    public Task WarmModelAsync(string? model = null, CancellationToken ct = default) => Task.CompletedTask;
}
