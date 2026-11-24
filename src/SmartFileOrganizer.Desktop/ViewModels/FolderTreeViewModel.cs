using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Sensitivity;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Desktop.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// Represents a single node in the folder tree.
/// Children are loaded lazily when expanded.
/// </summary>
public partial class FolderTreeItemViewModel : ViewModelBase
{
    private readonly DirectoryQueryService _dirQuery;
    private readonly FolderTreeViewModel? _owner;
    private readonly long _jobId;
    private bool _childrenLoaded;

    public DirectoryNode Node { get; }

    [ObservableProperty] private ObservableCollection<FolderTreeItemViewModel> _children = [];
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    /// <summary>Verdict, retention and size of the folder; null until the job's facts are read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFacts), nameof(VerdictLabel), nameof(VerdictColor), nameof(HasVerdict),
        nameof(RetentionLabel), nameof(RetentionColor), nameof(HasRetention), nameof(SizeText), nameof(IsOverridden),
        nameof(SensitiveText), nameof(HasSensitive), nameof(FactsToolTip))]
    private DirectoryItem? _facts;

    // Placeholder to make tree show expand arrow before children are loaded
    private static readonly FolderTreeItemViewModel Placeholder =
        new(null!, null!, 0, null);

    public FolderTreeItemViewModel(DirectoryNode node, DirectoryQueryService dirQuery, long jobId, FolderTreeViewModel? owner)
    {
        Node = node;
        _dirQuery = dirQuery;
        _jobId = jobId;
        _owner = owner;
        _facts = owner?.FactsOf(node.FullPath);
    }

    public string DisplayName => Node?.Name ?? "…";
    public string FullPath => Node?.FullPath ?? string.Empty;

    public bool HasFacts => Facts is not null;
    public bool HasVerdict => VerdictLabel.Length > 0;

    /// <summary>The verdict in a word; <c>Fehler</c> when the folder was judged without a valid answer.</summary>
    public string VerdictLabel => Facts is not { } f ? string.Empty
        : f.Verdict is not null ? BrowserDisplay.VerdictLabel(f.Verdict)
        : f.VerdictError is not null ? "Fehler" : string.Empty;

    public string VerdictColor => Facts is { Verdict: null, VerdictError: not null }
        ? BrowserDisplay.SensitiveColor
        : BrowserDisplay.VerdictColor(Facts?.Verdict);

    public bool HasRetention => Facts?.Retention is not null;
    public string RetentionLabel => BrowserDisplay.RetentionLabel(Facts?.Retention);
    public string RetentionColor => BrowserDisplay.RetentionColor(Facts?.Retention);
    public string SizeText => Facts is { } f ? PlanDisplay.Size(f.SizeBytes) : string.Empty;

    /// <summary>The verdict was set by a person.</summary>
    public bool IsOverridden => Facts?.VerdictSource == VerdictSource.Human.ToCode();

    public bool HasSensitive => Facts is { } f && (f.SensitiveFiles > 0 || f.Sensitive.Count > 0);

    /// <summary>Marked files below the folder, or that the folder itself is marked.</summary>
    public string SensitiveText => Facts is not { } f ? string.Empty
        : f.SensitiveFiles > 0 ? $"{f.SensitiveFiles} sensibel"
        : f.Sensitive.Count > 0 ? "sensibel" : string.Empty;

    public string? FactsToolTip
    {
        get
        {
            if (Facts is not { } f) return null;
            var lines = new List<string> { f.FullPath, $"{PlanDisplay.Files(f.Files)} mit Unterordnern, {PlanDisplay.Size(f.SizeBytes)}" };
            if (f.Verdict is not null)
                lines.Add($"{VerdictLabel} ({BrowserDisplay.SourceLabel(f.VerdictSource)})" + (HasRetention ? $", {RetentionLabel}" : string.Empty));
            else if (f.VerdictError is not null)
                lines.Add("Kein gültiges Urteil vom Modell");
            else
                lines.Add("Noch kein Urteil");
            if (f.Purpose is { Length: > 0 }) lines.Add(f.Purpose);
            if (f.AnalysisSkip is not null) lines.Add(BrowserDisplay.SkipText(f.AnalysisSkip));
            return string.Join(Environment.NewLine, lines);
        }
    }

    [RelayCommand]
    private void RevealInExplorer()
    {
        var path = Node?.FullPath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        new ShellLauncher().Reveal(path);
    }

    [RelayCommand]
    private async Task RescanDirectoryAsync()
    {
        if (_owner?.RescanDirectoryAction is not { } rescan || string.IsNullOrWhiteSpace(FullPath)) return;
        await rescan(FullPath);
    }

    public bool HasPlaceholder => Children.Count == 1 && Children[0] == Placeholder;

    public void AddPlaceholder()
    {
        if (Node?.SubdirCount > 0 && !_childrenLoaded)
            Children.Add(Placeholder);
    }

    /// <summary>Takes the facts from the tree again, here and in every loaded child.</summary>
    internal void RefreshFacts()
    {
        if (Node is null || _owner is null) return; // the placeholder
        Facts = _owner.FactsOf(Node.FullPath);
        foreach (var child in Children) child.RefreshFacts();
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
            var item = new FolderTreeItemViewModel(d, _dirQuery, _jobId, _owner);
            item.AddPlaceholder();
            Children.Add(item);
        }
    }
}

/// <summary>
/// The folder tree of a job. The folders appear at once; verdict, retention and size follow when the job's facts are
/// read (one pass over its files, off the UI thread), and are read again without rebuilding the tree
/// (https://github.com/crack666/smart_file_organizer/issues/14).
/// </summary>
public partial class FolderTreeViewModel : ViewModelBase
{
    private static readonly TimeSpan MinFactsInterval = TimeSpan.FromSeconds(5);

    private readonly DirectoryQueryService _dirQuery;
    private readonly JobReadService _jobRead;
    private readonly SensitiveOutputOptions _sensitive;

    private readonly SemaphoreSlim _factsGate = new(1, 1);
    private IReadOnlyDictionary<string, DirectoryItem> _facts = new Dictionary<string, DirectoryItem>();
    private DateTime _nextFactsUtc = DateTime.MinValue;
    private int _factsVersion;
    private long _jobId;

    [ObservableProperty] private ObservableCollection<FolderTreeItemViewModel> _roots = [];
    [ObservableProperty] private FolderTreeItemViewModel? _selectedItem;
    [ObservableProperty] private bool _isLoading;

    public Func<string, Task>? RescanDirectoryAction { get; set; }

    public event EventHandler<string>? DirectorySelected;

    /// <summary>The facts of the folders were read (again).</summary>
    public event EventHandler? FactsLoaded;

    public FolderTreeViewModel(DirectoryQueryService dirQuery, JobReadService jobRead, SensitiveOutputOptions sensitive)
    {
        _dirQuery = dirQuery;
        _jobRead = jobRead;
        // As in the plan view: a person at the local machine sees what is marked, the text of access data never.
        _sensitive = sensitive with { IncludeSensitive = true };
    }

    public async Task LoadJobAsync(long jobId, CancellationToken ct = default)
    {
        IsLoading = true;
        Roots.Clear();
        if (_jobId != jobId)
            _facts = new Dictionary<string, DirectoryItem>();
        _jobId = jobId;

        try
        {
            var roots = await _dirQuery.GetRootsAsync(jobId, ct);
            foreach (var d in roots)
            {
                var item = new FolderTreeItemViewModel(d, _dirQuery, jobId, this);
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
        await RefreshFactsAsync(ct);
    }

    public void Clear()
    {
        _jobId = 0;
        _facts = new Dictionary<string, DirectoryItem>();
        Roots.Clear();
        SelectedItem = null;
    }

    /// <summary>Facts of a folder by its full path; null while they are not read yet.</summary>
    public DirectoryItem? FactsOf(string path) => _facts.GetValueOrDefault(path);

    /// <summary>Reads the facts of every folder again and shows them; the open folders and the selection stay.</summary>
    public async Task RefreshFactsAsync(CancellationToken ct = default)
    {
        await _factsGate.WaitAsync(ct);
        try
        {
            var jobId = _jobId;
            if (jobId <= 0) return;
            IReadOnlyDictionary<string, DirectoryItem> facts;
            int version;
            var clock = Stopwatch.StartNew();
            do
            {
                version = _factsVersion;
                // SQLite answers synchronously, and a large job takes seconds.
                facts = await Task.Run(() => _jobRead.GetDirectoryItemsAsync(jobId, _sensitive, ct), ct);
            }
            while (version != _factsVersion && jobId == _jobId); // a verdict was set meanwhile: read once more

            // A job that takes long to read is read less often while the analysis runs.
            var wait = clock.Elapsed * 10;
            _nextFactsUtc = DateTime.UtcNow + (wait > MinFactsInterval ? wait : MinFactsInterval);
            if (jobId != _jobId) return; // another job was opened meanwhile

            _facts = facts;
            foreach (var root in Roots) root.RefreshFacts();
            FactsLoaded?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _factsGate.Release();
        }
    }

    /// <summary>For progress reports: reads the facts again when the last reading is long enough ago and none runs.</summary>
    public Task RefreshFactsIfDueAsync() =>
        _factsGate.CurrentCount == 0 || DateTime.UtcNow < _nextFactsUtc ? Task.CompletedTask : RefreshFactsAsync();

    /// <summary>Shows the verdict a person just set or took back, without reading the whole job again.</summary>
    public void ApplyVerdict(string path, DirectoryVerdict? verdict)
    {
        _factsVersion++;
        if (FactsOf(path) is not { } item) return;
        _facts = new Dictionary<string, DirectoryItem>(_facts, StringComparer.Ordinal)
        {
            [path] = item with
            {
                Verdict = verdict?.Verdict?.ToCode(),
                VerdictSource = verdict?.Source.ToCode(),
                VerdictRule = verdict?.Rule,
                Retention = verdict?.Retention?.ToCode(),
                VerdictError = verdict?.Error,
                Purpose = verdict?.Purpose,
                Reason = verdict?.Reason
            }
        };
        foreach (var root in Roots) root.RefreshFacts();
    }

    partial void OnSelectedItemChanged(FolderTreeItemViewModel? value)
    {
        Debug.WriteLine($"[TREE SELECT] value='{value?.FullPath ?? "(null)"}'  subscribers={DirectorySelected?.GetInvocationList().Length ?? 0}");
        if (value != null)
            DirectorySelected?.Invoke(this, value.FullPath);
    }
}
