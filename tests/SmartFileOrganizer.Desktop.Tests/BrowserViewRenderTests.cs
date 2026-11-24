using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Desktop.ViewModels;
using SmartFileOrganizer.Desktop.Views;
using SmartFileOrganizer.Domain.Enums;
using static SmartFileOrganizer.Desktop.Tests.PlanViewRenderTests;

namespace SmartFileOrganizer.Desktop.Tests;

/// <summary>
/// The browser of an analysed job in the real main window at 1300 px, rendered without a screen
/// (https://github.com/crack666/smart_file_organizer/issues/14): verdicts in the tree, the folder banner, filters and the
/// common correction, then the person's own verdict. With <c>SFO_SCREENSHOT_DIR</c> set the frames are saved there, from a
/// test folder <c>sfo-demo-browser</c> in the temp folder (docs/images holds them).
/// </summary>
public class BrowserViewRenderTests
{
    [AvaloniaFact]
    public async Task AnalysedJob_InTheMainWindow_TreeBannerFiltersAndOwnVerdict()
    {
        var shots = Environment.GetEnvironmentVariable("SFO_SCREENSHOT_DIR") is { Length: > 0 } d ? Directory.CreateDirectory(d).FullName : null;
        using var bed = new PlanTestbed(shots is null ? null : Path.Combine(Path.GetTempPath(), "sfo-demo-browser"));
        await bed.AnalyzeAsync();
        await bed.MarkExamplesAsync();

        var services = bed.GuiServices();
        var main = services.GetRequiredService<MainViewModel>();
        main.OllamaBaseUrl = "http://localhost:11434";
        var window = new MainWindow { DataContext = main, Width = 1300, Height = 900 };
        window.Show();
        var job = (await services.GetRequiredService<ScanJobService>().GetJobAsync(bed.JobId))!;
        await main.OpenRecentScanCommand.ExecuteAsync(job);
        await Until(() => main.FolderTree.Roots.SelectMany(r => r.Children).Count(c => c.HasFacts) == 3);

        // 1: the tree with verdict, retention and size; Downloads selected, two photos checked for a common category.
        var downloads = main.FolderTree.Roots.SelectMany(r => r.Children).Single(c => c.DisplayName == "Downloads");
        main.FolderTree.SelectedItem = downloads;
        await Until(() => main.Directory.Title == "Downloads" && main.FileTable.Rows.Count == 4);
        foreach (var row in main.FileTable.Rows.Where(r => r.Name.StartsWith("IMG_", StringComparison.Ordinal)))
            row.IsChecked = true;
        main.FileTable.BulkCategory = FileCategory.Screenshot;
        main.FileTable.SelectedRow = main.FileTable.Rows.Single(r => r.Name == "Einkaufsliste.txt");
        await Until(() => main.FileDetail.HasTextPreview);
        await Until(() => Visible(window).Contains("einheitlich") && Visible(window).Contains("2 Dateien ausgewählt"));
        Assert.Contains(Visible(window), t => t == "gemischt");
        Assert.Contains(Visible(window), t => t == "1 sensibel");
        Assert.Contains(Visible(window), t => t.StartsWith("Begründung: ", StringComparison.Ordinal));
        Assert.Contains(Visible(window), t => t == "niedrige Konfidenz");
        Assert.Contains(Visible(window), t => t == "Finanzen");
        Assert.Contains(Visible(window), t => t == "temporär");
        Frame(window, shots, "14-browser-tree-banner.png");

        // 2: the own verdict of the folder, in the editor and then saved.
        main.Directory.ToggleEditCommand.Execute(null);
        main.Directory.SelectedVerdict = main.Directory.VerdictOptions.Single(o => o.Kind == DirectoryVerdictKind.Uniform);
        main.Directory.SelectedRetention = DirectoryBannerViewModel.RetentionOptions.Single(o => o.Value == RetentionValue.Keep);
        main.Directory.Note = "nur Fotos vom Sommer";
        await Until(() => Visible(window).Any(t => t.StartsWith("Gleichartiger Inhalt", StringComparison.Ordinal)));
        Frame(window, shots, "15-folder-verdict-edit.png");
        await main.Directory.SaveOverrideCommand.ExecuteAsync(null);
        await Until(() => Visible(window).Any(t => t.StartsWith("Von Hand gesetzt am ", StringComparison.Ordinal)));
        Assert.True(downloads.IsOverridden);

        // 3: a sensitive file: its preview waits for the click.
        var privat = main.FolderTree.Roots.SelectMany(r => r.Children).Single(c => c.DisplayName == "Privat");
        main.FolderTree.SelectedItem = privat;
        await Until(() => main.FileTable.Rows.Count == 1);
        main.FileTable.SelectedRow = main.FileTable.Rows.Single();
        await Until(() => Visible(window).Any(t => t.StartsWith(BrowserDisplay.AccessDataHint, StringComparison.Ordinal)));
        Frame(window, shots, "16-sensitive-file.png");
        window.Close();
    }
}
