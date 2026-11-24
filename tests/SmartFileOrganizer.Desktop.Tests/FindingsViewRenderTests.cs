using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Desktop.ViewModels;
using SmartFileOrganizer.Desktop.Views;
using SmartFileOrganizer.Domain.Enums;
using static SmartFileOrganizer.Desktop.Tests.PlanViewRenderTests;

namespace SmartFileOrganizer.Desktop.Tests;

/// <summary>
/// The findings of an analysed and compared job in the real main window at 1300 px, rendered without a screen
/// (https://github.com/crack666/smart_file_organizer/issues/27): bundles with their findings, decided with the keyboard,
/// the target picker, a locked sensitive finding, the search, and the folder view with its file counts. With
/// <c>SFO_SCREENSHOT_DIR</c> set the frames are saved there, from a test folder <c>sfo-demo-findings</c> in the temp folder
/// (docs/images holds them).
/// </summary>
public class FindingsViewRenderTests
{
    [AvaloniaFact]
    public async Task ComparedJob_InTheMainWindow_DecidedWithTheKeyboard()
    {
        var shots = Environment.GetEnvironmentVariable("SFO_SCREENSHOT_DIR") is { Length: > 0 } d ? Directory.CreateDirectory(d).FullName : null;
        using var bed = new PlanTestbed(shots is null ? null : Path.Combine(Path.GetTempPath(), "sfo-demo-findings"), withMedia: true, withCopies: true);
        await bed.PrepareFindingsAsync();
        await bed.SqlAsync("""
            UPDATE ai_results SET confidence = 0.4, certainty = 'uncertain'
             WHERE file_node_id = (SELECT id FROM file_nodes WHERE name = 'Ausflug_2023.mp4');
            """);

        var services = bed.GuiServices();
        var main = services.GetRequiredService<MainViewModel>();
        main.OllamaBaseUrl = "http://localhost:11434";
        var window = new MainWindow { DataContext = main, Width = 1300, Height = 900 };
        window.Show();
        var job = (await services.GetRequiredService<ScanJobService>().GetJobAsync(bed.JobId))!;
        await main.OpenRecentScanCommand.ExecuteAsync(job);
        var vm = main.Findings;
        await Until(() => vm.Rows.Count > 0 && main.JobBar.Items.Any(i => i.HasAnalysis));

        // 1: the bundle with its picture strip, reason and action; jobs and quick filters at the side.
        vm.SelectedRows.Add(vm.Rows[0]);
        await vm.DetailLoad;
        await Until(() => vm.Pictures.Count == 3);
        await Until(() => Visible(window).Contains("Schnellfilter") && Visible(window).Contains("nicht gesichert"));
        foreach (var text in new[] { "Quelle", "Bibliothek", "prüfen", "doppelt", "Empfohlene Aktion", "Ordner · 03.07.2021 · 3 Dateien · 8,5 KB · 1 Fundstück, 1 offen" })
            Assert.Contains(text, Visible(window));
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "FindingList");
        Assert.Equal(1, list.SelectedItems!.Count);
        // Every button of the bar is inside the window at this width.
        foreach (var b in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0))
            Assert.True(b.TranslatePoint(new Point(b.Bounds.Width, b.Bounds.Height), window) is { X: <= 1300, Y: <= 900 }, $"{b.Content} outside");
        Frame(window, shots, "17-findings.png");

        // 2: the keyboard alone: down to the first finding, accept it, the next one is selected and has the keyboard.
        var first = vm.Rows.OfType<FindingRowViewModel>().First();
        (list.ContainerFromIndex(0) ?? throw new InvalidOperationException("no row")).Focus();
        Press(window, PhysicalKey.ArrowDown);
        await Until(() => vm.SelectedRows is [FindingRowViewModel r] && r == first);
        Press(window, PhysicalKey.A);
        await Until(() => first.Finding.State == FindingState.Accepted && vm.SelectedRows is [FindingRowViewModel r] && r != first);
        await Until(() => list.IsKeyboardFocusWithin);
        var second = (FindingRowViewModel)vm.SelectedRows.Single();
        Press(window, PhysicalKey.N);
        await Until(() => second.Finding.State == FindingState.Rejected && !vm.Rows.Contains(second));
        await Until(() => list.IsKeyboardFocusWithin && vm.SelectedRows.Count == 1);

        // Several rows with Shift; a bundle closed and opened with the arrows.
        Press(window, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
        await Until(() => vm.SelectedRows.Count == 2);
        vm.SelectedRows.Clear();
        vm.SelectedRows.Add(vm.Rows[0]);
        await Until(() => list.ContainerFromIndex(0) is not null);
        list.ContainerFromIndex(0)!.Focus();
        var rows = vm.Rows.Count;
        Press(window, PhysicalKey.ArrowLeft);
        await Until(() => vm.Rows.Count == rows - 1 && list.IsKeyboardFocusWithin);
        Press(window, PhysicalKey.ArrowRight);
        await Until(() => vm.Rows.Count == rows);

        // 3: the target of a finding from the folder tree of the library.
        vm.SelectedRows.Clear();
        var documents = vm.Rows.OfType<FindingRowViewModel>().Single(r => r.Title == "4 Dokumente · Downloads");
        vm.SelectedRows.Add(documents);
        await vm.DetailLoad;
        await Until(() => list.ContainerFromItem(documents) is not null);
        list.ContainerFromItem(documents)!.Focus();
        Press(window, PhysicalKey.Z);
        await Until(() => vm.TargetPicker is { Roots.Count: 1 } p && p.Roots[0].Children.Any(c => c.Label == "Fotos"));
        var picker = vm.TargetPicker!;
        picker.SelectedFolder = picker.Roots[0];
        picker.NewFolderName = "Dokumente";
        picker.AddFolderCommand.Execute(null);
        await Until(() => Visible(window).Contains("Neues Ziel: bibliothek/Dokumente"));
        Frame(window, shots, "18-findings-target.png");
        await picker.AcceptAsync();
        await Until(() => !vm.HasTargetPicker && vm.StatusText.Contains("Ziel bibliothek/Dokumente"));
        await vm.DetailLoad;
        Assert.Contains("umziehen → bibliothek/Dokumente", Visible(window));

        // 4: a sensitive finding: nothing of it before the click; the document before stays released.
        vm.SelectedRows.Clear();
        vm.SelectedRows.Add(vm.Rows.OfType<FindingRowViewModel>().Single(r => r.SingleDecision));
        await vm.DetailLoad;
        await Until(() => Visible(window).Any(t => t.StartsWith("Sensibel: Dateien und Vorschau", StringComparison.Ordinal)));
        Assert.DoesNotContain(Visible(window), t => t.Contains("passwoerter", StringComparison.Ordinal));
        Assert.Contains("einzeln", Visible(window));
        Frame(window, shots, "19-findings-sensitive.png");

        // 5: the search, as both views show it.
        main.SearchText = "img 2023";
        await main.SearchCommand.ExecuteAsync(null);
        await Until(() => Visible(window).Contains("2 Dateien") && vm.Rows.OfType<FindingRowViewModel>().Single().Title == "3 Bilder · Downloads");
        vm.SelectedRows.Add(vm.Rows.OfType<FindingRowViewModel>().Single());
        await vm.DetailLoad;
        await Until(() => vm.Pictures.Count == 3);
        Frame(window, shots, "20-findings-search.png");
        main.ShowFoldersCommand.Execute(null);
        await Until(() => Visible(window).Contains("2 Dateien in der Suche") && main.FolderTree.Roots.SelectMany(r => r.Children).All(c => c.HasFacts));
        main.ClearSearchCommand.Execute(null);

        // 6: the folder view with size and file count per folder, the largest first.
        main.FolderTree.SelectedSort = FolderSort.Files;
        main.FolderTree.SelectedItem = main.FolderTree.Roots[0].Children.Single(c => c.DisplayName == "Downloads");
        await Until(() => main.FileTable.Rows.Count == 8 && main.Directory.Title == "Downloads");
        await Until(() => Visible(window).Contains("8 Dateien") && Visible(window).Contains("nach Dateizahl"));
        Assert.Equal(["Downloads"], SelectedFolders(window));
        main.FolderTree.SelectedSort = FolderSort.Size;
        await Until(() => SelectedFolders(window) is ["Downloads"]);
        Assert.Same(main.FolderTree.Roots[0].Children.Single(c => c.DisplayName == "Downloads"), main.FolderTree.SelectedItem);
        Frame(window, shots, "21-folders-sorted.png");
        window.Close();
    }

    private static List<string> SelectedFolders(Window window) => window.GetVisualDescendants().OfType<TreeViewItem>()
        .Where(t => t.IsSelected && t.IsEffectivelyVisible).Select(t => ((FolderTreeItemViewModel)t.DataContext!).DisplayName).ToList();

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }
}
