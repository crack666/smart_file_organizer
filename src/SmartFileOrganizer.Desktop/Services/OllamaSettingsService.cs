using System.Text.Json;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Desktop.Services;

public class OllamaSettingsService
{
    private readonly OllamaOptions _options;
    private readonly string _settingsPath;

    public OllamaSettingsService(OllamaOptions options, string settingsPath)
    {
        _options = options;
        _settingsPath = settingsPath;
    }

    public Task SaveAsync(CancellationToken ct = default)
    {
        var payload = new
        {
            Ollama = new
            {
                _options.BaseUrl,
                _options.Model,
                _options.TimeoutSeconds,
                _options.MaxRetries,
                _options.KeepAlive,
                _options.SummaryLanguage,
                _options.SystemPrompt
            }
        };

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return File.WriteAllTextAsync(_settingsPath, json, ct);
    }
}