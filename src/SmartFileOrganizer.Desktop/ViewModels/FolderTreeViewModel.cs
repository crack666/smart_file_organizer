using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// Represents a single node in the folder tree.
/// Children are loaded lazily when expanded.
/// </summary>
public partial class FolderTreeItemViewModel : ViewModelBase
{
    private readonly DirectoryQueryService _dirQuery;
    private readonly long _jobId;
    private bool _childrenLoaded;

    public DirectoryNode Node { get; }

    [ObservableProperty] private ObservableCollection<FolderTreeItemViewModel> _children = [];
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    // Placeholder to make tree show expand arrow before children are loaded
    private static readonly FolderTreeItemViewModel Placeholder =
        new(null!, null!, 0);

    public FolderTreeItemViewModel(DirectoryNode node, DirectoryQueryService dirQuery, long jobId)
    {
        Node = node;
        _dirQuery = dirQuery;
        _jobId = jobId;
    }

    public string DisplayName => Node?.Name ?? "…";
    public string FullPath => Node?.FullPath ?? string.Empty;
    public string StatusBadge => Node?.DirStatus.ToString()[0..1] ?? "";
    [RelayCommand]
    private void RevealInExplorer()
    {
        var path = Node?.FullPath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = false
        });
    }
    public bool HasPlaceholder => Children.Count == 1 && Children[0] == Placeholder;

    public void AddPlaceholder()
    {
        if (Node?.SubdirCount > 0 && !_childrenLoaded)
            Children.Add(Placeholder);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_childrenLoaded)
            _ = LoadChildrenAsync();
    }

    private async Task LoadChildrenAsync()
    {
        _childrenLoaded = true;
        Children.Clear();

        var dirs = await _dirQuery.GetChildrenAsync(_jobId, Node.FullPath);
        foreach (var d in dirs)
        {
            var item = new FolderTreeItemViewModel(d, _dirQuery, _jobId);
            item.AddPlaceholder();
            Children.Add(item);
        }
    }
}

public partial class FolderTreeViewModel : ViewModelBase
{
    private readonly DirectoryQueryService _dirQuery;

    [ObservableProperty] private ObservableCollection<FolderTreeItemViewModel> _roots = [];
    [ObservableProperty] private FolderTreeItemViewModel? _selectedItem;
    [ObservableProperty] private bool _isLoading;

    public event EventHandler<string>? DirectorySelected;

    public FolderTreeViewModel(DirectoryQueryService dirQuery)
    {
        _dirQuery = dirQuery;
    }

    public async Task LoadJobAsync(long jobId, CancellationToken ct = default)
    {
        IsLoading = true;
        Roots.Clear();

        try
        {
            var roots = await _dirQuery.GetRootsAsync(jobId, ct);
            foreach (var d in roots)
            {
                var item = new FolderTreeItemViewModel(d, _dirQuery, jobId);
                item.AddPlaceholder();
                Roots.Add(item);
                // Auto-expand root so its subdirectories are immediately visible
                item.IsExpanded = true;
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedItemChanged(FolderTreeItemViewModel? value)
    {
        System.Diagnostics.Debug.WriteLine($"[TREE SELECT] value='{value?.FullPath ?? "(null)"}'  subscribers={DirectorySelected?.GetInvocationList().Length ?? 0}");
        if (value != null)
            DirectorySelected?.Invoke(this, value.FullPath);
    }
}
