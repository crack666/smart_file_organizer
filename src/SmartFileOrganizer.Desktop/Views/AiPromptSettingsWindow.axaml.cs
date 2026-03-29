using Avalonia.Controls;
using SmartFileOrganizer.Desktop.ViewModels;

namespace SmartFileOrganizer.Desktop.Views;

public partial class AiPromptSettingsWindow : Window
{
    public AiPromptSettingsWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => WireCallbacks();
        WireCallbacks();
    }

    private void WireCallbacks()
    {
        if (DataContext is AiPromptSettingsViewModel vm)
        {
            vm.ConfirmCallback = () => Close(true);
            vm.CancelCallback = () => Close(false);
        }
    }
}
