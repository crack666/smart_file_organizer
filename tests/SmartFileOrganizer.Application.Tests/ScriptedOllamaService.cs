using System.Collections.Concurrent;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Stand-in for Ollama in Phase 1 tests. Records every pre-assessment input and answers with
/// <see cref="Answer"/> (default: the verdict the profile suggests, else <c>mixed</c>). Never touches the network.
/// </summary>
internal sealed class ScriptedOllamaService : IOllamaService
{
    public ConcurrentBag<DirectoryPreAssessmentInput> PreAssessInputs { get; } = [];

    /// <summary>Verdict to answer for a directory name; return null to answer with an error.</summary>
    public Func<DirectoryPreAssessmentInput, DirectoryVerdictKind?> Answer { get; set; } =
        input => input.Profile.Suggests ?? DirectoryVerdictKind.Mixed;

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
            Source = VerdictSource.Model,
            Purpose = kind is null ? null : $"purpose of {input.Directory.Name}",
            Retention = kind is null ? null : RetentionValue.Keep,
            Reason = kind is null ? null : "scripted",
            ModelUsed = "scripted",
            JudgedAt = DateTime.UtcNow,
            Error = kind is null ? "scripted failure" : null
        };
        return Task.FromResult(new DirectoryPreAssessmentOutcome(assessment, verdict));
    }

    /// <summary>Phase 2 answer per file; null (default) means Phase 2 is not part of the test and throws.</summary>
    public Func<OllamaClassificationInput, FileClassification>? Classify { get; set; }

    /// <summary>Called before each classification, for example to cancel a run midway.</summary>
    public Action<OllamaClassificationInput>? BeforeClassify { get; set; }

    public ConcurrentBag<OllamaClassificationInput> ClassifyInputs { get; } = [];
    public ConcurrentBag<DirectorySummaryInput> SummaryInputs { get; } = [];

    public IReadOnlyList<string> ClassifiedNames => ClassifyInputs.Select(i => i.File.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>First looks only; second calls of a check have <see cref="EvidenceDepth.Extended"/>.</summary>
    public IReadOnlyList<OllamaClassificationInput> FirstLooks => ClassifyInputs.Where(i => i.Depth == EvidenceDepth.Standard).ToList();

    /// <summary>Second calls of a check.</summary>
    public IReadOnlyList<OllamaClassificationInput> SecondLooks => ClassifyInputs.Where(i => i.Depth == EvidenceDepth.Extended).ToList();

    public Task<FileClassification> ClassifyAsync(OllamaClassificationInput input, CancellationToken ct = default)
    {
        if (Classify is null) throw new NotSupportedException("Phase 2 is not part of these tests.");
        BeforeClassify?.Invoke(input);
        ct.ThrowIfCancellationRequested();
        ClassifyInputs.Add(input);
        var result = Classify(input);
        result.ModelUsed ??= "scripted";
        result.AnalyzedAt = DateTime.UtcNow;
        result.IsAiResult = true;
        return Task.FromResult(result);
    }

    public Task<DirectoryClassificationResult> SummarizeDirectoryAsync(DirectorySummaryInput input, CancellationToken ct = default)
    {
        SummaryInputs.Add(input);
        return Task.FromResult(new DirectoryClassificationResult
        {
            Phase = "summary",
            Summary = $"model summary of {input.Directory.Name}",
            ModelUsed = "scripted",
            AnalyzedAt = DateTime.UtcNow
        });
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<OllamaLocalModel>> GetLocalModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OllamaLocalModel>>([]);

    public Task WarmModelAsync(string? model = null, CancellationToken ct = default) => Task.CompletedTask;
}
