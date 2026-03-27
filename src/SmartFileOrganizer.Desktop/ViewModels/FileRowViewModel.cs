using CommunityToolkit.Mvvm.ComponentModel;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

/// <summary>
/// One row in the file table.
/// </summary>
public class FileRowViewModel : ViewModelBase
{
    public long Id { get; init; }
    public string FullPath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public string FileType { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public string LastModified { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Confidence { get; init; } = string.Empty;
    public string SuggestedTarget { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;

    public static FileRowViewModel From(FileNode f)
    {
        string category = string.Empty;
        string confidence = string.Empty;
        string suggestedTarget = string.Empty;

        if (f.Override is { } uo)
        {
            category = uo.OverriddenCategory.ToString() + " (override)";
            suggestedTarget = uo.OverriddenTarget ?? string.Empty;
        }
        else if (f.Classification is { } cls)
        {
            category = cls.Category.ToString();
            confidence = $"{cls.Confidence:P0}";
            suggestedTarget = cls.SuggestedTarget ?? string.Empty;
        }

        return new FileRowViewModel
        {
            Id = f.Id,
            FullPath = f.FullPath,
            Name = f.Name,
            Extension = f.Extension,
            FileType = f.FileType.ToString(),
            SizeText = FormatSize(f.Size),
            LastModified = f.LastWriteTime.ToString("yyyy-MM-dd"),
            Status = f.Status.ToString(),
            Category = category,
            Confidence = confidence,
            SuggestedTarget = suggestedTarget
        };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
