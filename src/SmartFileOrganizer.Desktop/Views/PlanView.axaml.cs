using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SmartFileOrganizer.Desktop.ViewModels;

namespace SmartFileOrganizer.Desktop.Views;

public partial class PlanView : UserControl
{
    public PlanView()
    {
        InitializeComponent();
        // Tunnelling, so the list's own key handling does not swallow Enter first.
        ActionList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Enter in the list opens the selected file in its default program, as a double click does.</summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        if (DataContext is not PlanViewModel { SelectedAction: not null } vm) return;
        if (e.Source is Visual source && source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null) return;
        vm.OpenSelectedCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// A double click on an action row opens its file in the default program, as "Öffnen" does (a video or sound file
    /// plays). Not on the tick box or a button of the row, and not on a group header.
    /// </summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not PlanViewModel vm || e.Source is not Visual source) return;
        if (source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null
            || source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if ((source as StyledElement)?.DataContext is not PlanActionRowViewModel row || !ReferenceEquals(row, vm.SelectedAction)) return;
        vm.OpenSelectedCommand.Execute(null);
        e.Handled = true;
    }
}
