using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Sensitivity;
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
    private FileNode? _file;
    private long _revealedFileId;
    private int _previewVersion;

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
    [ObservableProperty] private string _modelUsed = string.Empty;
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

    // ── Sensitive files ───────────────────────────────────────────────────
    /// <summary>The markers of the file in words; empty when it has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSensitive))]
    private string _sensitiveText = string.Empty;

    public bool IsSensitive => SensitiveText.Length > 0;

    /// <summary>A sensitive file: nothing of its content is read until the person asks for the preview.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowPreviewCommand))]
    private bool _isPreviewLocked;

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
        _file = file;
        var markers = SensitiveOutputGuard.GetMarkers(file);
        var accessData = SensitiveOutputOptions.IsAccessData(markers);
        SensitiveText = BrowserDisplay.MarkerText(markers);

        Name = file.Name;
        FullPath = file.FullPath;
        Extension = file.Extension;
        FileType = file.FileType.ToString();
        SizeText = FormatSize(file.Size);
        LastModified = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm");

        if (file.Classification is { } cls)
        {
            HasClassification = true;
            Category = SensitiveOutputGuard.ApplyReviewRule(cls.Category, markers).ToString();
            Importance = cls.Importance.ToString();
            Confidence = $"{cls.Confidence:P0}";
            Summary = accessData ? BrowserDisplay.AccessDataHint : cls.Summary ?? string.Empty;
            // The old free-text target of the model is no target (deprecated): targets lie only in a library.
            SuggestedTarget = string.Empty;
            ModelUsed = cls.ModelUsed ?? string.Empty;
        }
        else
        {
            HasClassification = false;
            Category = Importance = Confidence = Summary = SuggestedTarget = ModelUsed = string.Empty;
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
            SelectedCategory = file.Classification is { } c
                ? SensitiveOutputGuard.ApplyReviewRule(c.Category, markers)
                : FileCategory.Unknown;
            EditTarget = string.Empty;
        }

        UpdateReviewStatus(file.Status);

        if (_revealedFileId != file.Id) _revealedFileId = 0;
        if (accessData)
        {
            ClosePreview(BrowserDisplay.AccessDataHint + " Die Datei im Standardprogramm öffnen, um sie anzusehen.");
        }
        else if (markers != SensitiveMarker.None && _revealedFileId != file.Id)
        {
            ClosePreview(string.Empty);
            IsPreviewLocked = true;
        }
        else
        {
            IsPreviewLocked = false;
            await LoadPreviewAsync(file, ct);
        }
    }

    public void Clear()
    {
        _fileNodeId = 0;
        _file = null;
        _revealedFileId = 0;
        SensitiveText = string.Empty;
        ClosePreview(string.Empty);
        Name = FullPath = Extension = FileType = SizeText = LastModified = string.Empty;
        Category = Importance = Confidence = Summary = SuggestedTarget = ModelUsed = string.Empty;
        OverrideCategory = OverrideTarget = string.Empty;
        EditTarget = string.Empty;
        SelectedCategory = FileCategory.Unknown;
        HasClassification = HasOverride = false;
        ReviewStatusText = string.Empty;
        ReviewStatusColor = "#888888";
    }

    /// <summary>Shows the preview of a sensitive file, for this one file until another is selected.</summary>
    [RelayCommand(CanExecute = nameof(IsPreviewLocked))]
    private async Task ShowPreviewAsync()
    {
        if (_file is not { } file) return;
        _revealedFileId = file.Id;
        IsPreviewLocked = false;
        await LoadPreviewAsync(file, CancellationToken.None);
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
        ClosePreview(string.Empty);
        var version = _previewVersion;

        var preview = await _previewService.BuildAsync(file, ct);
        if (version != _previewVersion)
        {
            preview.Dispose(); // another file was selected meanwhile
            return;
        }
        // One picture here: for a video the first of its filmstrip (the plan view shows all of them).
        ImagePreview = preview.TakeStill();
        TextPreview = preview.TextPreview;
        PreviewMessage = preview.Message;
        PreviewChanged();
    }

    /// <summary>Removes the preview, and what is still being read for it; <paramref name="message"/> stands in its place.</summary>
    private void ClosePreview(string message)
    {
        _previewVersion++;
        ImagePreview?.Dispose();
        ImagePreview = null;
        TextPreview = string.Empty;
        PreviewMessage = message;
        IsPreviewLocked = false;
        PreviewChanged();
    }

    private void PreviewChanged()
    {
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
