using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// One folder of a library in the target picker. Its subfolders are read when it is opened, through the folder view of
/// the library (<see cref="LibraryTargetService.FolderAsync"/>, the service of <c>sfo library folder</c>): from the scan,
/// without a model. A folder made with "Neuer Ordner hier" exists only as a name until a run creates it.
/// </summary>
public partial class PlanTargetFolderNode : ViewModelBase
{
    private readonly Func<PlanTargetFolderNode, Task<IReadOnlyList<PlanTargetFolderNode>>>? _load;
    private bool _loaded;

    public PlanTargetFolderNode(
        ScanJob library, string relativePath, string fullPath, string label, bool hasChildren,
        Func<PlanTargetFolderNode, Task<IReadOnlyList<PlanTargetFolderNode>>>? load, string kind = "")
    {
        Library = library;
        RelativePath = relativePath;
        FullPath = fullPath;
        Label = label;
        Kind = kind;
        _load = load;
        // A stand-in child, so the tree offers to open a folder before its subfolders are read.
        if (hasChildren && load is not null)
            Children.Add(Placeholder(library));
        else
            _loaded = true;
    }

    private PlanTargetFolderNode(ScanJob library) : this(library, string.Empty, string.Empty, "…", false, null, "placeholder") { }

    private static PlanTargetFolderNode Placeholder(ScanJob library) => new(library);

    public ScanJob Library { get; }

    /// <summary>Relative to the library root, with <c>/</c>; empty for the root.</summary>
    public string RelativePath { get; }

    public string FullPath { get; }
    public string Label { get; }

    /// <summary><c>root</c>, <c>inbox</c> (<c>inbox-new</c> when the scan has none yet), <c>new</c>, <c>placeholder</c>,
    /// or empty for a folder of the scan.</summary>
    public string Kind { get; }

    public bool IsPlaceholder => Kind == "placeholder";
    public bool IsNew => Kind is "new" or "inbox-new";
    public bool IsInbox => Kind is "inbox" or "inbox-new";

    public string Note => Kind switch
    {
        "inbox" => "Eingangsordner",
        "inbox-new" => "Eingangsordner, wird beim Ausführen angelegt",
        "new" => "neu, wird beim Ausführen angelegt",
        "root" => "Bibliothek",
        _ => string.Empty
    };

    public bool HasNote => Note.Length > 0;

    public ObservableCollection<PlanTargetFolderNode> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = LoadAsync();
    }

    /// <summary>Reads the subfolders once.</summary>
    public async Task LoadAsync()
    {
        if (_loaded || _load is null) return;
        _loaded = true;
        var children = await _load(this);
        Children.Clear();
        foreach (var c in children)
            Children.Add(c);
    }
}

/// <summary>
/// "Ziel ändern" (https://github.com/crack666/smart_file_organizer/issues/102): a folder of the plan's libraries for one
/// action or a selection, chosen in a tree of the libraries with the inbox folder first and "Neuer Ordner hier". The
/// action keeps its own name below the chosen folder; the plan's checks decide whether the target is allowed, and a
/// refused target leaves the picker open with the reason.
/// </summary>
public partial class PlanTargetPickerViewModel : ViewModelBase
{
    private readonly LibraryTargetService _libraries;
    private readonly Func<string, Task<string?>> _apply;
    private readonly TaskCompletionSource<bool> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="apply">Sets the targets below the chosen folder (full path); returns why it was refused, or null.</param>
    public PlanTargetPickerViewModel(
        IReadOnlyList<PlanActionRowViewModel> rows, IReadOnlyList<ScanJob> libraries, LibraryTargetService libraryService,
        Func<string, Task<string?>> apply)
    {
        _libraries = libraryService;
        _apply = apply;
        Rows = rows;
        Libraries = libraries;
        Title = rows.Count == 1 ? $"Ziel für {rows[0].Name}" : $"Ziel für {rows.Count} Aktionen";
        CurrentText = rows.Count == 1 ? "Jetzt: " + rows[0].ShortTargetText : string.Empty;
        ReasonText = rows.Count == 1 ? rows[0].Action.Reason : string.Join(Environment.NewLine,
            rows.Select(r => r.Action.Reason).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().Take(3));
    }

    public IReadOnlyList<PlanActionRowViewModel> Rows { get; }
    public IReadOnlyList<ScanJob> Libraries { get; }
    public string Title { get; }
    public string CurrentText { get; }

    /// <summary>Why the plan proposed what it proposed, shown beside the tree.</summary>
    public string ReasonText { get; }

    public ObservableCollection<PlanTargetFolderNode> Roots { get; } = [];

    [ObservableProperty] private PlanTargetFolderNode? _selectedFolder;
    [ObservableProperty] private string _newFolderName = string.Empty;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public bool HasError => ErrorText.Length > 0;

    /// <summary>The new target of the first action below the chosen folder.</summary>
    public string PreviewText => SelectedFolder is { IsPlaceholder: false } f
        ? "Neues Ziel: " + PlanActionRowViewModel.ShortTarget(
            PlanChecks.Join(f.FullPath, Rows[0].Name.TrimEnd('/')), Libraries.Select(l => l.RootPath).ToList())
          + (Rows.Count > 1 ? $" (und {Rows.Count - 1} weitere in denselben Ordner)" : string.Empty)
        : "Einen Ordner wählen.";

    public bool CanAccept => SelectedFolder is { IsPlaceholder: false } && !IsBusy;

    /// <summary>Completes when the picker is closed: true when a target was set.</summary>
    public Task<bool> Closed => _closed.Task;

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnSelectedFolderChanged(PlanTargetFolderNode? value)
    {
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(CanAccept));
        AcceptCommand.NotifyCanExecuteChanged();
        AddFolderCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAccept));
        AcceptCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The libraries of the plan as roots, each with its inbox folder first and its top level read.</summary>
    public async Task LoadAsync()
    {
        Roots.Clear();
        foreach (var library in Libraries)
        {
            var root = new PlanTargetFolderNode(library, string.Empty, library.RootPath,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(library.RootPath)) is { Length: > 0 } n ? n : library.RootPath,
                hasChildren: true, ChildrenAsync, "root");
            Roots.Add(root);
            root.IsExpanded = true;
            await root.LoadAsync();
        }
    }

    private async Task<IReadOnlyList<PlanTargetFolderNode>> ChildrenAsync(PlanTargetFolderNode parent)
    {
        var list = new List<PlanTargetFolderNode>();
        try
        {
            var detail = await _libraries.FolderAsync(parent.Library.Id, parent.RelativePath, limit: 1, offset: 0);
            var inbox = parent.Kind == "root" ? _libraries.Options.InboxFolderOf(parent.Library.RootPath) : null;
            if (inbox is { Length: > 0 })
            {
                var existing = detail.Subfolders.FirstOrDefault(s => string.Equals(s.RelativePath.Replace('\\', '/'), inbox, StringComparison.OrdinalIgnoreCase));
                list.Add(new PlanTargetFolderNode(parent.Library, inbox, PlanChecks.Join(parent.Library.RootPath, inbox), inbox,
                    existing is { Children.Count: > 0 }, ChildrenAsync, existing is null ? "inbox-new" : "inbox"));
            }
            foreach (var s in detail.Subfolders.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var rel = s.RelativePath.Replace('\\', '/');
                if (inbox is not null && string.Equals(rel, inbox, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new PlanTargetFolderNode(parent.Library, rel, s.FullPath, s.Name, s.Children.Count > 0, ChildrenAsync));
            }
        }
        catch (LibraryServiceException ex)
        {
            ErrorText = ex.Message;
        }
        return list;
    }

    /// <summary>"Neuer Ordner hier": a folder name below the chosen folder, made when a run copies into it.</summary>
    [RelayCommand(CanExecute = nameof(CanAddFolder))]
    private void AddFolder()
    {
        if (SelectedFolder is not { IsPlaceholder: false } parent) return;
        var name = NewFolderName.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        {
            ErrorText = $"„{name}“ ist kein Ordnername.";
            return;
        }
        var rel = parent.RelativePath.Length == 0 ? name : parent.RelativePath + "/" + name;
        var node = new PlanTargetFolderNode(parent.Library, rel, PlanChecks.Join(parent.FullPath, name), name, false, null, "new");
        parent.Children.Insert(0, node);
        parent.IsExpanded = true;
        NewFolderName = string.Empty;
        ErrorText = string.Empty;
        SelectedFolder = node;
    }

    private bool CanAddFolder() => SelectedFolder is { IsPlaceholder: false };

    /// <summary>Sets the target through the plan; on a refusal the reason is shown and the picker stays open.</summary>
    [RelayCommand(CanExecute = nameof(CanAccept))]
    public async Task AcceptAsync()
    {
        if (SelectedFolder is not { IsPlaceholder: false } folder) return;
        IsBusy = true;
        try
        {
            var refused = await _apply(folder.FullPath);
            if (refused is null)
            {
                _closed.TrySetResult(true);
                return;
            }
            ErrorText = refused;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void Cancel() => _closed.TrySetResult(false);
}
