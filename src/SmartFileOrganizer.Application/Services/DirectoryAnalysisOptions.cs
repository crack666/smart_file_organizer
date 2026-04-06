namespace SmartFileOrganizer.Application.Services;

/// <summary>
/// Lightweight options for the directory analysis pipeline.
/// Values are populated from OllamaOptions at startup via ServiceConfigurator.
/// </summary>
public class DirectoryAnalysisOptions
{
    public int MaxSamplesPerDirectory { get; set; } = 15;
}
