using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Desktop.ViewModels;

namespace SmartFileOrganizer.Desktop.Views;

public partial class FindingsView : UserControl
{
    private FindingsViewModel? _vm;

    public FindingsView()
    {
        InitializeComponent();
        // Tunnelling, so the list's own key handling does not swallow the keys first.
        FindingList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Decided -= FocusList;
            _vm = DataContext as FindingsViewModel;
            if (_vm is not null) _vm.Decided += FocusList;
        };
    }

    /// <summary>A accept, Z change the target, N reject, O open again, left and right close and open a bundle, Enter shows a
    /// sensitive preview.</summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || _vm is not { } vm) return;
        var group = vm.SelectedRows.LastOrDefault() as FindingGroupViewModel;
        IRelayCommand? command = e.Key switch
        {
            Key.A => vm.AcceptCommand,
            Key.Z => vm.ChangeTargetCommand,
            Key.N => vm.RejectCommand,
            Key.O => vm.ReopenCommand,
            Key.Enter => vm.ShowPreviewCommand,
            Key.Left when group is { IsExpanded: true } => vm.ToggleGroupCommand,
            Key.Right when group is { IsExpanded: false } => vm.ToggleGroupCommand,
            _ => null
        };
        if (command is null) return;
        if (command.CanExecute(group)) command.Execute(group);
        if (e.Key is Key.Left or Key.Right) FocusList();
        e.Handled = true;
    }

    /// <summary>The list was filled again: the keys go on working at its selected row.</summary>
    private void FocusList() => Dispatcher.UIThread.Post(() =>
    {
        if (_vm?.SelectedRows.LastOrDefault() is not { } row || _vm.HasTargetPicker) return;
        FindingList.ScrollIntoView(row);
        (FindingList.ContainerFromItem(row) ?? FindingList).Focus();
    }, DispatcherPriority.Background);

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as StyledElement)?.DataContext is FindingFileItem file)
            _vm?.OpenFileCommand.Execute(file.FullPath);
    }
}
