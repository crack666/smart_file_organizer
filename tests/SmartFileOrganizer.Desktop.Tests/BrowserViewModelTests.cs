using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Desktop.ViewModels;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.Tests;

/// <summary>
/// The browser on an analysed job (https://github.com/crack666/smart_file_organizer/issues/14): verdicts in the tree,
/// the folder banner and the person's own verdict, the filters of the file table and the common correction, the
/// locked preview of sensitive files, and the CSV export. The job is made by <c>sfo eval --stub</c> on invented files.
/// </summary>
public class BrowserViewModelTests : IDisposable
{
    private readonly PlanTestbed _bed = new();

    public void Dispose()
    {
        _bed.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The analysed job, with one shaky, one temporary and one finance file, opened in the main view model.</summary>
    private async Task<MainViewModel> OpenAsync()
    {
        await _bed.AnalyzeAsync();
        await _bed.MarkExamplesAsync();
        var main = _bed.GuiServices().GetRequiredService<MainViewModel>();
        var job = (await _bed.GuiServices().GetRequiredService<ScanJobService>().GetJobAsync(_bed.JobId))!;
        await main.OpenRecentScanCommand.ExecuteAsync(job);
        await Until(() => Folder(main, "Downloads") is { HasFacts: true });
        return main;
    }

    private static FolderTreeItemViewModel? Folder(MainViewModel main, string name) =>
        main.FolderTree.Roots.SelectMany(r => r.Children).FirstOrDefault(c => c.DisplayName == name);

    private static async Task<FolderTreeItemViewModel> SelectAsync(MainViewModel main, string name)
    {
        var folder = Folder(main, name)!;
        main.FolderTree.SelectedItem = folder;
        await Until(() => main.Directory.Title == name && main.FileTable.Rows.Count > 0 && !main.FileTable.IsLoading);
        return folder;
    }

    private static List<string> Names(FileTableViewModel table) => table.Rows.Select(r => r.Name).ToList();

    [AvaloniaFact]
    public async Task Tree_ShowsVerdictRetentionAndSize_AndTheBannerWhatTheJobKnows()
    {
        var main = await OpenAsync();

        var holiday = Folder(main, "Urlaub Kroatien 2021")!;
        Assert.Equal("einheitlich", holiday.VerdictLabel);
        Assert.Equal("Modell", BrowserDisplay.SourceLabel(holiday.Facts!.VerdictSource));
        Assert.True(holiday.HasRetention);
        Assert.Equal(holiday.Facts.SizeBytes, new DirectoryInfo(Path.Combine(_bed.Tree, "Urlaub Kroatien 2021")).GetFiles().Sum(f => f.Length));
        Assert.EndsWith(" KB", holiday.SizeText);
        Assert.False(holiday.HasSensitive);
        Assert.Equal("1 sensibel", Folder(main, "Privat")!.SensitiveText);
        Assert.Equal("gemischt", Folder(main, "Downloads")!.VerdictLabel);
        // The root is the whole job: its size covers every folder.
        var root = main.FolderTree.Roots.Single();
        Assert.Equal(8, root.Facts!.Files);

        await SelectAsync(main, "Downloads");
        var banner = main.Directory;
        Assert.True(banner.IsVisible);
        Assert.Equal("gemischt", banner.VerdictLabel);
        Assert.Equal("Modell", banner.SourceText);
        Assert.True(banner.HasRetention);
        Assert.NotEmpty(banner.Reason);
        Assert.StartsWith("4 Dateien · ", banner.SizeText);
        Assert.False(banner.IsOverridden);
        Assert.Empty(banner.EscalationText);
    }

    [AvaloniaFact]
    public async Task OwnVerdict_IsStored_ShownInTreeAndBanner_AndTakenBack()
    {
        var main = await OpenAsync();
        var folder = await SelectAsync(main, "Downloads");
        var banner = main.Directory;

        banner.ToggleEditCommand.Execute(null);
        Assert.True(banner.IsEditing);
        Assert.Equal(DirectoryVerdictKind.Mixed, banner.SelectedVerdict!.Kind);
        Assert.Equal(["Einheit", "einheitlich", "gemischt", "System"], banner.VerdictOptions.Select(o => o.Label));
        banner.SelectedVerdict = banner.VerdictOptions.Single(o => o.Kind == DirectoryVerdictKind.Uniform);
        Assert.StartsWith("Gleichartiger Inhalt", banner.EffectText);
        banner.SelectedRetention = DirectoryBannerViewModel.RetentionOptions.Single(o => o.Value == RetentionValue.Keep);
        banner.Note = "alles Fotos vom Sommer";
        await banner.SaveOverrideCommand.ExecuteAsync(null);

        Assert.False(banner.IsEditing);
        Assert.Equal(("einheitlich", "von Hand", "behalten"), (banner.VerdictLabel, banner.SourceText, banner.RetentionLabel));
        Assert.Equal("alles Fotos vom Sommer", banner.Reason);
        Assert.Matches(@"^Von Hand gesetzt am \d\d\.\d\d\.\d{4}; vorher: gemischt \(Modell\)$", banner.OverrideText);
        Assert.True(banner.IsOverridden);
        Assert.Equal(("einheitlich", true, "behalten"), (folder.VerdictLabel, folder.IsOverridden, folder.RetentionLabel));
        Assert.Equal(1L, await _bed.ScalarAsync($"""
            SELECT COUNT(*) FROM v_directories WHERE job_id = {_bed.JobId} AND relative_path = 'Downloads'
               AND verdict = 'uniform' AND verdict_source = 'human' AND retention = 'keep'
            """));

        // The tree read again from the database says the same.
        await main.FolderTree.RefreshFactsAsync();
        Assert.Equal(("einheitlich", true), (Folder(main, "Downloads")!.VerdictLabel, Folder(main, "Downloads")!.IsOverridden));

        await banner.ResetOverrideCommand.ExecuteAsync(null);
        Assert.Equal(("gemischt", "Modell", false), (banner.VerdictLabel, banner.SourceText, banner.IsOverridden));
        Assert.Equal(("gemischt", false), (folder.VerdictLabel, folder.IsOverridden));
        Assert.Equal(0L, await _bed.ScalarAsync("SELECT COUNT(*) FROM directory_overrides"));
    }

    [AvaloniaFact]
    public async Task Filters_ByCategoryStateConfidenceSensitiveAndTemporary_AndStayForTheNextFolder()
    {
        var main = await OpenAsync();
        await SelectAsync(main, "Downloads");
        var table = main.FileTable;
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal("4 Dateien", table.CountText);
        Assert.False(table.IsFiltered);

        table.SelectedCategoryFilter = FileTableViewModel.CategoryFilters.Single(o => o.Value == FileCategory.Photo);
        Assert.Equal(["IMG_20230812_143012.jpg", "IMG_20230812_143512.jpg"], Names(table));
        Assert.Equal("2 von 4 Dateien", table.CountText);
        Assert.True(table.IsFiltered);
        table.OnlyTemporary = true;
        Assert.Equal(["IMG_20230812_143512.jpg"], Names(table));
        table.ResetFiltersCommand.Execute(null);
        Assert.Equal(4, table.Rows.Count);

        table.OnlyLowConfidence = true;
        Assert.Equal(["Einkaufsliste.txt"], Names(table));
        Assert.Contains("70", table.LowConfidenceHint);
        // Confirmed by a person, it is no question any more.
        await table.Rows.Single().ToggleAcceptCommand.ExecuteAsync(null);
        table.OnlyLowConfidence = false;
        table.OnlyLowConfidence = true;
        Assert.Empty(table.Rows);
        Assert.Equal("Kein Filtertreffer in diesem Ordner.", table.EmptyText);
        table.ResetFiltersCommand.Execute(null);

        table.SelectedStatusFilter = FileTableViewModel.StatusFilters.Single(o => o.Value == FileNodeStatus.Approved);
        Assert.Equal(["Einkaufsliste.txt"], Names(table));
        Assert.Equal("bestätigt", table.Rows.Single().StatusText);
        table.ResetFiltersCommand.Execute(null);

        table.OnlySensitive = true;
        Assert.Equal(["Rechnung_Strom.pdf"], Names(table));
        Assert.Equal("Finanzen", table.Rows.Single().SensitiveText);

        // The filter holds in the next folder too.
        await SelectAsync(main, "Privat");
        Assert.Equal(["passwoerter.txt"], Names(table));
        Assert.Equal("Zugangsdaten", table.Rows.Single().SensitiveText);
        Assert.Equal(BrowserDisplay.AccessDataHint, table.Rows.Single().Summary);
    }

    [AvaloniaFact]
    public async Task CommonCorrection_SetsTheCategoryOfTheCheckedRows_AndNeverOfHiddenOnes()
    {
        var main = await OpenAsync();
        await SelectAsync(main, "Downloads");
        var table = main.FileTable;
        Assert.False(table.ApplyCategoryToCheckedCommand.CanExecute(null));

        table.CheckAllShownCommand.Execute(null);
        Assert.Equal(4, table.CheckedCount);
        Assert.Equal("4 Dateien ausgewählt", table.CheckedText);
        // A filter hides two of them: their marks go, so the correction cannot reach them.
        table.SelectedCategoryFilter = FileTableViewModel.CategoryFilters.Single(o => o.Value == FileCategory.Photo);
        Assert.Equal(2, table.CheckedCount);
        table.ResetFiltersCommand.Execute(null);
        Assert.Equal(2, table.CheckedCount);
        Assert.All(table.Rows.Where(r => r.IsChecked), r => Assert.StartsWith("IMG_", r.Name));

        table.BulkCategory = FileCategory.Screenshot;
        Assert.True(table.ApplyCategoryToCheckedCommand.CanExecute(null));
        await table.ApplyCategoryToCheckedCommand.ExecuteAsync(null);

        Assert.Equal("2 Dateien: Kategorie Screenshot gesetzt.", table.BulkStatus);
        Assert.Equal(0, table.CheckedCount);
        var photos = table.Rows.Where(r => r.Name.StartsWith("IMG_", StringComparison.Ordinal)).ToList();
        Assert.All(photos, r => Assert.Equal((FileCategory.Screenshot, "overridden", FileNodeStatus.Approved), (r.SelectedCategory, r.ReviewStatus, r.Status)));
        Assert.Equal(2L, await _bed.ScalarAsync($"""
            SELECT COUNT(*) FROM user_overrides u JOIN file_nodes f ON f.id = u.file_node_id
             WHERE f.job_id = {_bed.JobId} AND u.overridden_category = {(int)FileCategory.Screenshot}
            """));
        Assert.Equal(2L, await _bed.ScalarAsync($"SELECT COUNT(*) FROM file_nodes WHERE job_id = {_bed.JobId} AND status = {(int)FileNodeStatus.Approved}"));

        // Shown again from the database: the same.
        await main.FolderTree.RefreshFactsAsync();
        await SelectAsync(main, "Privat");
        await SelectAsync(main, "Downloads");
        table.SelectedCategoryFilter = FileTableViewModel.CategoryFilters.Single(o => o.Value == FileCategory.Screenshot);
        Assert.Equal(["IMG_20230812_143012.jpg", "IMG_20230812_143512.jpg"], Names(table));
    }

    [AvaloniaFact]
    public async Task SensitiveFile_ShowsItsPreviewOnlyAfterAClick_AccessDataNever()
    {
        var main = await OpenAsync();
        await SelectAsync(main, "Downloads");
        var detail = main.FileDetail;

        main.FileTable.SelectedRow = main.FileTable.Rows.Single(r => r.Name == "Rechnung_Strom.pdf");
        await Until(() => detail.Name == "Rechnung_Strom.pdf");
        Assert.Equal("Finanzen", detail.SensitiveText);
        Assert.True(detail.IsPreviewLocked);
        Assert.False(detail.HasTextPreview || detail.HasImagePreview || detail.HasPreviewMessage);
        await detail.ShowPreviewCommand.ExecuteAsync(null);
        Assert.False(detail.IsPreviewLocked);
        Assert.True(detail.HasTextPreview || detail.HasImagePreview || detail.HasPreviewMessage);

        // Another file and back: locked again.
        main.FileTable.SelectedRow = main.FileTable.Rows.Single(r => r.Name == "Einkaufsliste.txt");
        await Until(() => detail.HasTextPreview);
        Assert.False(detail.IsSensitive);
        Assert.Contains("Batterien", detail.TextPreview);
        main.FileTable.SelectedRow = main.FileTable.Rows.Single(r => r.Name == "Rechnung_Strom.pdf");
        await Until(() => detail.IsPreviewLocked);

        await SelectAsync(main, "Privat");
        main.FileTable.SelectedRow = main.FileTable.Rows.Single();
        await Until(() => detail.Name == "passwoerter.txt");
        Assert.False(detail.IsPreviewLocked);
        Assert.False(detail.HasTextPreview);
        Assert.StartsWith(BrowserDisplay.AccessDataHint, detail.PreviewMessage);
        Assert.Equal(BrowserDisplay.AccessDataHint, detail.Summary);
        Assert.DoesNotContain("dummy-not-real", detail.TextPreview + detail.Summary + detail.PreviewMessage);
    }

    [AvaloniaFact]
    public async Task CsvButton_WritesEveryFileOfTheJob_ToTheChosenFile()
    {
        var main = await OpenAsync();
        Assert.True(main.ExportCsvCommand.CanExecute(null));
        var target = Path.Combine(_bed.Root, "export", "liste.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string? suggested = null;
        main.PickSaveFileDialog = name =>
        {
            suggested = name;
            return Task.FromResult<string?>(target);
        };

        await main.ExportCsvCommand.ExecuteAsync(null);

        Assert.Equal($"sfo-job-{_bed.JobId}.csv", suggested);
        Assert.Equal($"CSV-Export fertig: 8 Dateien in {target}", main.ExportStatusText);
        var lines = await File.ReadAllLinesAsync(target);
        Assert.Equal(string.Join(',', ScanExportService.Columns), lines[0].TrimStart('﻿'));
        Assert.Equal(9, lines.Length);
        Assert.Contains(lines, l => l.Contains("Einkaufsliste.txt", StringComparison.Ordinal));
        // Access data leaves the program without its text, as with sfo export.
        Assert.DoesNotContain(lines, l => l.Contains("dummy-not-real", StringComparison.Ordinal));

        // Cancelled dialog: nothing happens.
        main.PickSaveFileDialog = _ => Task.FromResult<string?>(null);
        await main.ExportCsvCommand.ExecuteAsync(null);
        Assert.StartsWith("CSV-Export fertig", main.ExportStatusText);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition not met in time");
    }
}
