using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Desktop.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

public partial class FileDetailViewModel : ViewModelBase
{
    private readonly FilePreviewService _previewService;
    private readonly ReviewService _reviewService;

    private long _fileNodeId;

    // ── File metadata ──────────────────────────────────────────────────────
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _fullPath = string.Empty;
    [ObservableProperty] private string _extension = string.Empty;
    [ObservableProperty] private string _fileType = string.Empty;
    [ObservableProperty] private string _sizeText = string.Empty;
    [ObservableProperty] private string _lastModified = string.Empty;

    // ── AI classification (read-only display) ─────────────────────────────
    [ObservableProperty] private string _category = string.Empty;
    [ObservableProperty] private string _importance = string.Empty;
    [ObservableProperty] private string _confidence = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _suggestedTarget = string.Empty;
    [ObservableProperty] private bool _hasClassification;

    // ── Existing override (read-only display) ─────────────────────────────
    [ObservableProperty] private string _overrideCategory = string.Empty;
    [ObservableProperty] private string _overrideTarget = string.Empty;
    [ObservableProperty] private bool _hasOverride;

    // ── Review status ─────────────────────────────────────────────────────
    [ObservableProperty] private string _reviewStatusText = string.Empty;
    [ObservableProperty] private string _reviewStatusColor = "#888888";

    // ── Editable review fields ────────────────────────────────────────────
    [ObservableProperty] private FileCategory _selectedCategory = FileCategory.Unknown;
    [ObservableProperty] private string _editTarget = string.Empty;

    // ── Preview ───────────────────────────────────────────────────────────
    [ObservableProperty] private Bitmap? _imagePreview;
    [ObservableProperty] private string _textPreview = string.Empty;
    [ObservableProperty] private string _previewMessage = string.Empty;

    public bool HasImagePreview => ImagePreview != null;
    public bool HasTextPreview => !string.IsNullOrWhiteSpace(TextPreview);
    public bool HasPreviewMessage => !string.IsNullOrWhiteSpace(PreviewMessage);

    public static IReadOnlyList<FileCategory> AvailableCategories { get; } =
        Enum.GetValues<FileCategory>().ToList();

    /// <summary>Raised after Accept or SaveOverride so the table row refreshes.</summary>
    public event EventHandler? ReviewApplied;

    public FileDetailViewModel(FilePreviewService previewService, ReviewService reviewService)
    {
        _previewService = previewService;
        _reviewService = reviewService;
    }

    public async Task ApplyAsync(FileNode file, CancellationToken ct = default)
    {
        _fileNodeId = file.Id;

        Name = file.Name;
        FullPath = file.FullPath;
        Extension = file.Extension;
        FileType = file.FileType.ToString();
        SizeText = FormatSize(file.Size);
        LastModified = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm");

        if (file.Classification is { } cls)
        {
            HasClassification = true;
            Category = cls.Category.ToString();
            Importance = cls.Importance.ToString();
            Confidence = $"{cls.Confidence:P0}";
            Summary = cls.Summary ?? string.Empty;
            SuggestedTarget = cls.SuggestedTarget ?? string.Empty;
        }
        else
        {
            HasClassification = false;
            Category = Importance = Confidence = Summary = SuggestedTarget = string.Empty;
        }

        if (file.Override is { } uo)
        {
            HasOverride = true;
            OverrideCategory = uo.OverriddenCategory.ToString();
            OverrideTarget = uo.OverriddenTarget ?? string.Empty;
            SelectedCategory = uo.OverriddenCategory;
            EditTarget = uo.OverriddenTarget ?? string.Empty;
        }
        else
        {
            HasOverride = false;
            OverrideCategory = OverrideTarget = string.Empty;
            SelectedCategory = file.Classification?.Category ?? FileCategory.Unknown;
            EditTarget = file.Classification?.SuggestedTarget ?? string.Empty;
        }

        UpdateReviewStatus(file.Status);
        await LoadPreviewAsync(file, ct);
    }

    public void Clear()
    {
        _fileNodeId = 0;
        ImagePreview?.Dispose();
        Name = FullPath = Extension = FileType = SizeText = LastModified = string.Empty;
        Category = Importance = Confidence = Summary = SuggestedTarget = string.Empty;
        OverrideCategory = OverrideTarget = string.Empty;
        EditTarget = string.Empty;
        SelectedCategory = FileCategory.Unknown;
        ImagePreview = null;
        TextPreview = PreviewMessage = string.Empty;
        HasClassification = HasOverride = false;
        ReviewStatusText = string.Empty;
        ReviewStatusColor = "#888888";

        OnPropertyChanged(nameof(HasImagePreview));
        OnPropertyChanged(nameof(HasTextPreview));
        OnPropertyChanged(nameof(HasPreviewMessage));
    }

    [RelayCommand]
    private async Task AcceptAsync()
    {
        if (_fileNodeId <= 0) return;
        await _reviewService.AcceptAsync(_fileNodeId);
        ReviewStatusText = "✓ Accepted";
        ReviewStatusColor = "#4EC94E";
        ReviewApplied?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task SaveOverrideAsync()
    {
        if (_fileNodeId <= 0) return;
        await _reviewService.ApplyOverrideAsync(_fileNodeId, SelectedCategory, EditTarget.Trim(), note: null);
        HasOverride = true;
        OverrideCategory = SelectedCategory.ToString();
        OverrideTarget = EditTarget.Trim();
        ReviewStatusText = "✎ Overridden";
        ReviewStatusColor = "#E8A838";
        ReviewApplied?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateReviewStatus(FileNodeStatus status)
    {
        (ReviewStatusText, ReviewStatusColor) = status switch
        {
            FileNodeStatus.Approved when HasOverride => ("✎ Overridden", "#E8A838"),
            FileNodeStatus.Approved                  => ("✓ Accepted",   "#4EC94E"),
            FileNodeStatus.AiAnalyzed                => ("○ Pending review", "#888888"),
            FileNodeStatus.Error                     => ("⚠ Error",      "#E05050"),
            _                                        => ("○ Pending",    "#888888")
        };
    }

    private async Task LoadPreviewAsync(FileNode file, CancellationToken ct)
    {
        ImagePreview?.Dispose();
        ImagePreview = null;
        TextPreview = string.Empty;
        PreviewMessage = string.Empty;

        var preview = await _previewService.BuildAsync(file, ct);
        ImagePreview = preview.ImagePreview;
        TextPreview = preview.TextPreview;
        PreviewMessage = preview.Message;

        OnPropertyChanged(nameof(HasImagePreview));
        OnPropertyChanged(nameof(HasTextPreview));
        OnPropertyChanged(nameof(HasPreviewMessage));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
