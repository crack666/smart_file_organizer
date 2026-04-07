using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

public partial class FileRowViewModel : ViewModelBase
{
    private readonly ReviewService _reviewService;
    private readonly Func<FileRowViewModel, Task>? _reanalyzeFileAction;
    private bool _isInitializing = true;

    // ── Static / init-only ────────────────────────────────────────────────
    public long Id           { get; init; }
    public string FullPath   { get; init; } = string.Empty;
    public string ParentPath { get; init; } = string.Empty;
    public string Name       { get; init; } = string.Empty;
    public string Extension  { get; init; } = string.Empty;
    public string FileType   { get; init; } = string.Empty;
    public string SizeText   { get; init; } = string.Empty;
    public string LastModified { get; init; } = string.Empty;
    public string Confidence { get; init; } = string.Empty;
    public string Summary    { get; init; } = string.Empty;

    // ── Observable (interactive) ──────────────────────────────────────────
    [ObservableProperty] private FileCategory _selectedCategory = FileCategory.Unknown;
    [ObservableProperty] private string _editTarget = string.Empty;
    [ObservableProperty] private string _reviewStatus = "pending"; // pending | accepted | overridden

    // ── Computed display ──────────────────────────────────────────────────
    public string CategoryColor       => GetCategoryColor(SelectedCategory);
    public string ReviewStatusDisplay => ReviewStatus switch { "accepted" => "✓", "overridden" => "✎", _ => "○" };
    public string ReviewStatusColor   => ReviewStatus switch { "accepted" => "#4EC94E", "overridden" => "#E8A838", _ => "#666666" };
    public string ReviewStatusTooltip => ReviewStatus switch { "accepted" => "Accepted — click to reset", "overridden" => "Overridden — click to reset", _ => "Pending — click to accept" };

    public static IReadOnlyList<FileCategory> AllCategories { get; } = Enum.GetValues<FileCategory>().ToList();

    public FileRowViewModel(ReviewService reviewService, Func<FileRowViewModel, Task>? reanalyzeFileAction = null)
    {
        _reviewService = reviewService;
        _reanalyzeFileAction = reanalyzeFileAction;
    }

    partial void OnSelectedCategoryChanged(FileCategory value)
    {
        if (_isInitializing) return;
        OnPropertyChanged(nameof(CategoryColor));
        _ = SaveOverrideAsync();
    }

    [RelayCommand]
    private async Task ToggleAcceptAsync()
    {
        if (Id <= 0) return;
        if (ReviewStatus is "accepted" or "overridden")
        {
            await _reviewService.ResetReviewAsync(Id);
            ReviewStatus = "pending";
            // Restore editable fields to original AI suggestion values
            // (SelectedCategory and EditTarget stay as-is visually —
            //  the override record is gone, so next load will show AI values)
        }
        else
        {
            await _reviewService.AcceptAsync(Id);
            ReviewStatus = "accepted";
        }
        OnPropertyChanged(nameof(ReviewStatusDisplay));
        OnPropertyChanged(nameof(ReviewStatusColor));
        OnPropertyChanged(nameof(ReviewStatusTooltip));
    }

    [RelayCommand]
    private async Task CommitTargetAsync()
    {
        if (Id <= 0) return;
        await _reviewService.ApplyOverrideAsync(Id, SelectedCategory, EditTarget.Trim(), null);
        ReviewStatus = "overridden";
        OnPropertyChanged(nameof(ReviewStatusDisplay));
        OnPropertyChanged(nameof(ReviewStatusColor));
        OnPropertyChanged(nameof(ReviewStatusTooltip));
    }

    [RelayCommand]
    private async Task ReanalyzeAsync()
    {
        if (_reanalyzeFileAction == null || Id <= 0) return;
        await _reanalyzeFileAction(this);
    }

    private async Task SaveOverrideAsync()
    {
        if (Id <= 0) return;
        await _reviewService.ApplyOverrideAsync(Id, SelectedCategory, EditTarget.Trim(), null);
        ReviewStatus = "overridden";
        OnPropertyChanged(nameof(ReviewStatusDisplay));
        OnPropertyChanged(nameof(ReviewStatusColor));
        OnPropertyChanged(nameof(ReviewStatusTooltip));
    }

    public static FileRowViewModel From(FileNode f, ReviewService reviewService, Func<FileRowViewModel, Task>? reanalyzeFileAction = null)
    {
        var vm = new FileRowViewModel(reviewService, reanalyzeFileAction)
        {
            Id           = f.Id,
            FullPath     = f.FullPath,
            ParentPath   = f.ParentPath,
            Name         = f.Name,
            Extension    = f.Extension,
            FileType     = f.FileType.ToString(),
            SizeText     = FormatSize(f.Size),
            LastModified = f.LastWriteTime.ToString("yyyy-MM-dd"),
            Confidence   = f.Classification is { } cls ? $"{cls.Confidence:P0}" : string.Empty,
            Summary      = f.Classification?.Summary ?? string.Empty,
        };

        if (f.Override is { } uo)
        {
            vm.EditTarget        = uo.OverriddenTarget ?? string.Empty;
            vm.SelectedCategory  = uo.OverriddenCategory;
            vm.ReviewStatus      = "overridden";
        }
        else if (f.Classification is { } c)
        {
            vm.EditTarget        = c.SuggestedTarget ?? string.Empty;
            vm.SelectedCategory  = c.Category;
            vm.ReviewStatus      = f.Status == FileNodeStatus.Approved ? "accepted" : "pending";
        }

        vm._isInitializing = false;
        return vm;
    }

    public static string GetCategoryColor(FileCategory cat) => cat switch
    {
        FileCategory.Photo                                                           => "#C2185B",
        FileCategory.Video                                                           => "#7B1FA2",
        FileCategory.Audio                                                           => "#512DA8",
        FileCategory.Document or FileCategory.DocumentScan or FileCategory.Letter   => "#1565C0",
        FileCategory.PersonalDocument or FileCategory.Invoice                       => "#1976D2",
        FileCategory.Screenshot                                                      => "#00796B",
        FileCategory.Code                                                            => "#2E7D32",
        FileCategory.SoftwareInstaller                                               => "#00838F",
        FileCategory.Archive                                                         => "#E65100",
        FileCategory.TrashCandidate                                                  => "#B71C1C",
        FileCategory.SystemFile                                                      => "#546E7A",
        FileCategory.ReviewNeeded                                                    => "#F57F17",
        _                                                                            => "#424242"
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024               => $"{bytes} B",
        < 1024 * 1024        => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _                    => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
