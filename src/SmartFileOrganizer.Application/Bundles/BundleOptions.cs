namespace SmartFileOrganizer.Application.Bundles;

/// <summary>
/// Thresholds of the bundle builder, configuration section <c>Bundles</c>
/// (https://github.com/crack666/smart_file_organizer/issues/39). These are starting values; see
/// docs/bundles.md for why they were chosen.
/// </summary>
public sealed class BundleOptions
{
    /// <summary>A pause of more than this many hours between two photos starts a new event. Default 8:
    /// a night separates two days, so events on consecutive days stay apart.</summary>
    public double EventGapHours { get; set; } = 8;

    /// <summary>An event spanning more days than this is split at its largest pauses until every part fits.
    /// Default 14: a two-week holiday stays one event, a camera roll used every day does not become one.</summary>
    public double MaxEventDays { get; set; } = 14;

    /// <summary>Groups with fewer files are not events; their files go to the year's <c>scattered</c> bundle. Default 3.</summary>
    public int MinEventFiles { get; set; } = 3;

    /// <summary>Pattern file that replaces the shipped <c>bundle-patterns.json</c>; null for the shipped one.</summary>
    public string? PatternsFile { get; set; }

    /// <summary>Throws <see cref="ArgumentException"/> for values the builder cannot work with.</summary>
    public void Validate()
    {
        if (EventGapHours <= 0) throw new ArgumentException("Bundles:EventGapHours must be greater than 0.");
        if (MaxEventDays <= 0) throw new ArgumentException("Bundles:MaxEventDays must be greater than 0.");
        if (MinEventFiles < 1) throw new ArgumentException("Bundles:MinEventFiles must be at least 1.");
    }
}
