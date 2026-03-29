using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.ViewModels;

public partial class FileTableViewModel : ViewModelBase
{
    private readonly FileQueryService _fileQuery;
    private readonly ReviewService _reviewService;

    [ObservableProperty] private ObservableCollection<FileRowViewModel> _rows = [];
    [ObservableProperty] private FileRowViewModel? _selectedRow;
    [ObservableProperty] private bool _isLoading;

    public FileTableViewModel(FileQueryService fileQuery, ReviewService reviewService)
    {
        _fileQuery = fileQuery;
        _reviewService = reviewService;
    }

    public async Task LoadAsync(long jobId, string directoryPath, CancellationToken ct = default)
    {
        IsLoading = true;
        Rows.Clear();
        SelectedRow = null;

        try
        {
            var files = await _fileQuery.GetFilesInDirectoryAsync(jobId, directoryPath, ct);
            System.Diagnostics.Debug.WriteLine($"[FILE TABLE] jobId={jobId} path='{directoryPath}' → {files.Count} file(s) returned");
            foreach (var f in files)
                Rows.Add(FileRowViewModel.From(f, _reviewService));
            System.Diagnostics.Debug.WriteLine($"[FILE TABLE] Rows.Count after add = {Rows.Count}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Clear()
    {
        Rows.Clear();
        SelectedRow = null;
    }
}
