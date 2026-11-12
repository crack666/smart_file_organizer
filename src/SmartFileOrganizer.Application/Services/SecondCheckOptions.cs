namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// When a Phase 2 result is checked a second time (https://github.com/crack666/smart_file_organizer/issues/23).
/// Populated at startup by AddSmartFileOrganizerCore (Hosting) from the <c>SecondCheck</c> section.
/// </summary>
/// <remarks>
/// Starting values, to be set from the measurement of https://github.com/crack666/smart_file_organizer/issues/10.
/// The confidence a model states about itself is weakly calibrated, so the threshold only catches answers in
/// which the model itself signals doubt; the contradiction checks carry most of the weight.
/// </remarks>
public class SecondCheckOptions
{
    /// <summary>Off: no second call is made; results that would need one are stored as <c>uncertain</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A model answer with a stated confidence below this value is checked a second time.</summary>
    public double BelowConfidence { get; set; } = 0.7;

    /// <summary>Check answers whose category is ReviewNeeded or Unknown.</summary>
    public bool CheckUnclearCategory { get; set; } = true;

    /// <summary>Check answers that contradict the retention of their directory verdict.</summary>
    public bool CheckVerdictContradictions { get; set; } = true;

    /// <summary>Other files of the directory named, with their category, in the second call.</summary>
    public int MaxNeighbours { get; set; } = 20;
}
