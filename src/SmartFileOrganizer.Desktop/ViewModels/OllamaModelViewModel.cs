using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

public class OllamaModelViewModel : ViewModelBase
{
    public string Name { get; init; } = string.Empty;
    public string Family { get; init; } = string.Empty;
    public string ParameterSize { get; init; } = string.Empty;
    public string QuantizationLevel { get; init; } = string.Empty;
    public string DisplayLabel { get; init; } = string.Empty;

    public static OllamaModelViewModel From(OllamaLocalModel model) => new()
    {
        Name = model.Name,
        Family = model.Family,
        ParameterSize = model.ParameterSize,
        QuantizationLevel = model.QuantizationLevel,
        DisplayLabel = model.DisplayLabel
    };
}