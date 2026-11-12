namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Thresholds of the staged analysis (sample first, single judgement for mixed directories).
/// Populated at startup by AddSmartFileOrganizerCore (Hosting) from the <c>Sampling</c> section;
/// <see cref="MaxSamplesPerDirectory"/> falls back to <c>Ollama:MaxSamplesPerDirectory</c>.
/// </summary>
/// <remarks>
/// The defaults lean towards judging too much rather than inheriting wrongly. How well the real model
/// agrees with itself is measured in https://github.com/crack666/smart_file_organizer/issues/10; adjust
/// the values from there.
/// </remarks>
public class DirectoryAnalysisOptions
{
    /// <summary>
    /// Members of a sample that are not sensitive, i.e. the most model calls a uniform directory costs
    /// before the rest inherits. Sensitive files come on top. Values below 3 are raised to 3.
    /// </summary>
    public int MaxSamplesPerDirectory { get; set; } = 15;

    /// <summary>
    /// Largest share of sample members in another category than the majority (failed calls count as
    /// deviating) that still confirms the uniform verdict. Above it the directory becomes mixed.
    /// 0.1 tolerates one outlier in 15, two escalate.
    /// </summary>
    public double MaxDeviationShare { get; set; } = 0.1;

    /// <summary>
    /// Highest confidence an inherited result may carry, so it never looks as certain as a measured one.
    /// The inherited value is the lowest confidence of the agreeing members, capped here.
    /// </summary>
    public double InheritedConfidenceCap { get; set; } = 0.6;

    /// <summary>Sample size actually used.</summary>
    public int EffectiveSampleSize => Math.Max(3, MaxSamplesPerDirectory);
}
