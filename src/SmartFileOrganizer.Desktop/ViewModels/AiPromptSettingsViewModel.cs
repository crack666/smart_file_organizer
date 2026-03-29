using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Desktop.ViewModels;

public partial class AiPromptSettingsViewModel : ViewModelBase
{
    [ObservableProperty] private string _systemPrompt = string.Empty;
    [ObservableProperty] private string _selectedLanguage = "English";

    public static IReadOnlyList<string> AvailableLanguages { get; } =
    [
        "English", "Deutsch", "Français", "Español", "Italiano",
        "Nederlands", "Polski", "Português", "Русский", "日本語", "中文"
    ];

    /// <summary>
    /// Set by the dialog window to signal save confirmation.
    /// </summary>
    public Action? ConfirmCallback { get; set; }

    /// <summary>
    /// Set by the dialog window to signal cancellation.
    /// </summary>
    public Action? CancelCallback { get; set; }

    public void Load(OllamaOptions options)
    {
        SystemPrompt = options.SystemPrompt;
        SelectedLanguage = options.SummaryLanguage;
    }

    [RelayCommand]
    private void ResetToDefault()
    {
        SystemPrompt = OllamaOptions.DefaultSystemPrompt;
    }

    [RelayCommand]
    private void Confirm()
    {
        ConfirmCallback?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelCallback?.Invoke();
    }
}
