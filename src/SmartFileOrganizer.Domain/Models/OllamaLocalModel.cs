namespace SmartFileOrganizer.Domain.Models;

public class OllamaLocalModel
{
    public string Name { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string ParameterSize { get; set; } = string.Empty;
    public string QuantizationLevel { get; set; } = string.Empty;

    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(ParameterSize)
            ? Name
            : $"{Name} ({ParameterSize}{(string.IsNullOrWhiteSpace(QuantizationLevel) ? string.Empty : $", {QuantizationLevel}")})";
}