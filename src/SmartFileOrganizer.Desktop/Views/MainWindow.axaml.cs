using Avalonia.Controls;
using Avalonia.Platform.Storage;
using SmartFileOrganizer.Desktop.ViewModels;

namespace SmartFileOrganizer.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        if (DataContext is MainViewModel vm)
            WireDialogs(vm);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel mainVm)
                WireDialogs(mainVm);
        };
    }

    private void WireDialogs(MainViewModel vm)
    {
        vm.PickFolderDialog = async () =>
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select root folder to scan",
                AllowMultiple = false
            });
            return result.Count > 0 ? result[0].TryGetLocalPath() : null;
        };

        vm.OpenPromptSettingsDialog = async dialogVm =>
        {
            var dialog = new AiPromptSettingsWindow { DataContext = dialogVm };
            var confirmed = await dialog.ShowDialog<bool?>(this);
            return confirmed == true;
        };
    }
}
