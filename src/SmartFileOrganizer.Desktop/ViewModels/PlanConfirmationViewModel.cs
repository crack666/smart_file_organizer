using CommunityToolkit.Mvvm.Input;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// The explicit confirmation before a step that changes files: what it will do, in a few lines, and one button that
/// does it. Shown over the plan view; nothing runs until <see cref="AcceptCommand"/>.
/// </summary>
public partial class PlanConfirmationViewModel : ViewModelBase
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PlanConfirmationViewModel(string title, IReadOnlyList<string> lines, string confirmText, bool isDanger)
    {
        Title = title;
        Lines = lines;
        ConfirmText = confirmText;
        IsDanger = isDanger;
    }

    public string Title { get; }
    public IReadOnlyList<string> Lines { get; }
    public string ConfirmText { get; }

    /// <summary>The step takes originals away (into the hold folder); the button is drawn in red.</summary>
    public bool IsDanger { get; }

    public string ConfirmColor => IsDanger ? "#C0392B" : "#2F6FD6";

    public Task<bool> Answer => _answer.Task;

    [RelayCommand]
    private void Accept() => _answer.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => _answer.TrySetResult(false);
}
