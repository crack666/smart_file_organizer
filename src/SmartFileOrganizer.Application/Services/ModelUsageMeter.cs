namespace SmartFileOrganizer.Application.Services;

/// <summary>Codes of the model calls counted by <see cref="ModelUsageMeter"/>; see docs/glossary.md.</summary>
public static class ModelCallKinds
{
    public const string PreAssessment = "pre_assessment";
    public const string Classification = "classification";
    public const string SecondCheck = "second_check";
    public const string Summary = "summary";

    /// <summary>All codes in the order of the pipeline.</summary>
    public static readonly IReadOnlyList<string> All = [PreAssessment, Classification, SecondCheck, Summary];
}

/// <summary>
/// What the model calls of one kind cost so far, from the counts the server returns with every answer.
/// <paramref name="CachedPromptTokens"/> is the part of <paramref name="PromptTokens"/> the server took from its prompt cache.
/// </summary>
/// <param name="MeanSeconds">Mean of prompt and answer time per call, as the server measured it.</param>
public sealed record ModelCallUsage(
    string Kind, int Calls, long PromptTokens, long CachedPromptTokens, long AnswerTokens, double MeanSeconds, int CutOff);

/// <summary>
/// Counts model calls and their tokens per kind for the current process (one <c>sfo analyze</c> or <c>sfo eval</c> run).
/// Thread-safe: the classification runs several calls at once. Holds numbers only, never prompt or answer text.
/// </summary>
public sealed class ModelUsageMeter
{
    private sealed class Totals
    {
        public int Calls;
        public long Prompt;
        public long Cached;
        public long Answer;
        public double Seconds;
        public int CutOff;
    }

    private readonly Dictionary<string, Totals> _byKind = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Records one answered call. <paramref name="cutOff"/>: the answer stopped at the token limit.</summary>
    public void Record(string kind, long promptTokens, long cachedPromptTokens, long answerTokens, TimeSpan duration, bool cutOff = false)
    {
        lock (_gate)
        {
            if (!_byKind.TryGetValue(kind, out var t))
                _byKind[kind] = t = new Totals();
            t.Calls++;
            t.Prompt += promptTokens;
            t.Cached += cachedPromptTokens;
            t.Answer += answerTokens;
            t.Seconds += duration.TotalSeconds;
            if (cutOff) t.CutOff++;
        }
    }

    /// <summary>The kinds with at least one call, in pipeline order.</summary>
    public IReadOnlyList<ModelCallUsage> Snapshot()
    {
        lock (_gate)
        {
            return _byKind
                .OrderBy(e => ModelCallKinds.All.ToList().IndexOf(e.Key) is var i && i < 0 ? int.MaxValue : i)
                .Select(e => new ModelCallUsage(e.Key, e.Value.Calls, e.Value.Prompt, e.Value.Cached, e.Value.Answer,
                    Math.Round(e.Value.Seconds / e.Value.Calls, 3), e.Value.CutOff))
                .ToList();
        }
    }
}
