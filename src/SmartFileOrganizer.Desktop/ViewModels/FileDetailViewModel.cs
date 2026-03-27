using CommunityToolkit.Mvvm.ComponentModel;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

public partial class FileDetailViewModel : ViewModelBase
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _fullPath = string.Empty;
    [ObservableProperty] private string _extension = string.Empty;
    [ObservableProperty] private string _fileType = string.Empty;
    [ObservableProperty] private string _sizeText = string.Empty;
    [ObservableProperty] private string _lastModified = string.Empty;
    [ObservableProperty] private string _category = string.Empty;
    [ObservableProperty] private string _importance = string.Empty;
    [ObservableProperty] private string _confidence = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _suggestedTarget = string.Empty;
    [ObservableProperty] private string _overrideCategory = string.Empty;
    [ObservableProperty] private string _overrideTarget = string.Empty;
    [ObservableProperty] private bool _hasOverride;
    [ObservableProperty] private bool _hasClassification;

    public void Apply(FileNode file)
    {
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
        }
        else
        {
            HasOverride = false;
            OverrideCategory = OverrideTarget = string.Empty;
        }
    }

    public void Clear()
    {
        Name = FullPath = Extension = FileType = SizeText = LastModified = string.Empty;
        Category = Importance = Confidence = Summary = SuggestedTarget = string.Empty;
        OverrideCategory = OverrideTarget = string.Empty;
        HasClassification = HasOverride = false;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
