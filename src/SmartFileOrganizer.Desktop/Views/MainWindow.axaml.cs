using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace SmartFileOrganizer.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        if (DataContext is ViewModels.MainViewModel vm)
            WireDialogs(vm);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainViewModel mainVm)
                WireDialogs(mainVm);
        };
    }

    private void WireDialogs(ViewModels.MainViewModel vm)
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
    }
}
