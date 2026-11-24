using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Desktop.ViewModels;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Desktop.Tests;

/// <summary>
/// The findings view on an analysed and compared job (https://github.com/crack666/smart_file_organizer/issues/27): accept,
/// change and reject, several at once, search, quick filters, previews, language, the jobs at the side and the sorted
/// folder tree. The job is made by <c>sfo eval --stub</c>, <c>compare</c>, <c>bundles build</c> and <c>decide</c> on
/// invented files; nothing is decided in it.
/// </summary>
public sealed class FindingsViewModelTests : IDisposable
{
    private readonly PlanTestbed _bed = new(withMedia: true, withCopies: true);
    private ServiceProviderHolder? _services;

    public void Dispose() => _bed.Dispose();

    private sealed record ServiceProviderHolder(IServiceProvider Services);

    private T Get<T>() where T : notnull => (_services ??= new(_bed.GuiServices())).Services.GetRequiredService<T>();

    private async Task<MainViewModel> OpenAsync()
    {
        await _bed.PrepareFindingsAsync();
        // What a stand-in model never answers: a shaky result.
        await _bed.SqlAsync("""
            UPDATE ai_results SET confidence = 0.4, certainty = 'uncertain'
             WHERE file_node_id = (SELECT id FROM file_nodes WHERE name = 'Ausflug_2023.mp4');
            """);
        var main = Get<MainViewModel>();
        await main.OpenRecentScanCommand.ExecuteAsync(await Job(_bed.JobId));
        return main;
    }

    private async Task<ScanJob> Job(long id) => (await Get<ScanJobService>().GetJobAsync(id))!;

    private static List<FindingRowViewModel> Shown(FindingsViewModel vm) => vm.Rows.OfType<FindingRowViewModel>().ToList();

    private static FindingRowViewModel Row(FindingsViewModel vm, string title) => Shown(vm).Single(r => r.Title == title);

    private static void Select(FindingsViewModel vm, params object[] rows)
    {
        vm.SelectedRows.Clear();
        foreach (var r in rows) vm.SelectedRows.Add(r);
    }

    private Task<long> Decisions(string where) =>
        _bed.ScalarAsync($"SELECT COUNT(*) FROM finding_decisions WHERE job_id = {_bed.JobId} AND {where}");

    [AvaloniaFact]
    public async Task FinishedSourceJob_OpensWithItsFindings_GroupedByBundle()
    {
        var main = await OpenAsync();
        var vm = main.Findings;

        Assert.True(main.IsFindingsVisible);
        Assert.False(main.IsBrowserVisible);
        Assert.Equal("7 Fundstücke · 7 offen", vm.CountText);
        var groups = vm.Rows.OfType<FindingGroupViewModel>().ToList();
        Assert.Equal(["Urlaub Kroatien 2021", "Alte Kopien", "Ohne Bündel", "Privat"], groups.Take(4).Select(g => g.Title));
        // Bundles without a finding come last and can still be looked at.
        Assert.All(groups.Skip(4), g => Assert.False(g.HasFindings));
        Assert.Equal("Ordner · 03.07.2021 · 3 Dateien · 8,5 KB · 1 Fundstück, 1 offen", groups[0].Summary);

        var holiday = Row(vm, "3 Bilder · Urlaub Kroatien 2021");
        Assert.Equal("nicht gesichert", holiday.KindLabel);
        Assert.Equal("umziehen → Fotos (laut Zielstruktur)", holiday.ActionText);
        Assert.Equal("03.07.2021 · 8,5 KB", holiday.FactsText);
        Assert.Equal("hier entfernen: 1 überzählig", Row(vm, "2 Dateien · Alte Kopien").ActionText);
        Assert.Equal(("offen", "sicher"), (holiday.StateLabel, holiday.CertaintyLabel));
        Assert.Contains("sfo findings targets", vm.MissingText);

        // The other order: one list by importance, without groups.
        vm.SelectedGrouping = FindingGrouping.Rank;
        Assert.Equal(7, vm.Rows.Count);
        Assert.Equal(vm.All.Select(r => r.Key), Shown(vm).Select(r => r.Key));

        // Folders are the second view.
        main.ShowFoldersCommand.Execute(null);
        Assert.True(main.IsBrowserVisible);
        Assert.False(main.IsFindingsVisible);
    }

    [AvaloniaFact]
    public async Task Accept_StoresTheDecisionOfThePerson_GoesOnToTheNext_AndThePlanTakesIt()
    {
        var vm = (await OpenAsync()).Findings;
        var holiday = Row(vm, "3 Bilder · Urlaub Kroatien 2021");
        Assert.False(vm.AcceptCommand.CanExecute(null));
        Select(vm, holiday);
        Assert.True(vm.AcceptCommand.CanExecute(null));
        vm.Note = "ja, alle";
        var decided = 0;
        vm.Decided += () => decided++;

        await vm.AcceptCommand.ExecuteAsync(null);

        Assert.Equal(("angenommen", "1 Fundstück „angenommen“."), (holiday.StateLabel, vm.StatusText));
        Assert.Equal(1, decided);
        Assert.Equal(1L, await Decisions($"finding_key = '{holiday.Key}' AND state = 'accepted' AND origin = 'human' AND note = 'ja, alle'"));
        Assert.Empty(vm.Note);
        Assert.Equal("7 Fundstücke · 6 offen · 1 angenommen", vm.CountText);
        // Accepted ones stay in the default list; the selection is on the next open finding.
        Assert.Contains(holiday, Shown(vm));
        Assert.Equal("2 Bilder · Alte Kopien", Assert.IsType<FindingRowViewModel>(vm.SelectedRows.Single()).Title);

        // Once more changes nothing.
        Select(vm, holiday);
        Assert.Contains("ja, alle", vm.DetailState);
        await vm.AcceptCommand.ExecuteAsync(null);
        Assert.Equal("Schon „angenommen“.", vm.StatusText);

        var plan = (await _bed.OkAsync("plan", "create", _bed.JobId.ToString())).GetProperty("plan_id").GetInt64();
        Assert.Equal(1L, await _bed.ScalarAsync($"SELECT COUNT(*) FROM plan_actions WHERE plan_id = {plan} AND finding_key = '{holiday.Key}'"));
    }

    [AvaloniaFact]
    public async Task Reject_HidesTheFinding_UntilItsStateIsShown_AndReopenTakesItBack()
    {
        var vm = (await OpenAsync()).Findings;
        var copies = Row(vm, "2 Bilder · Alte Kopien");
        Select(vm, copies);

        await vm.RejectCommand.ExecuteAsync(null);

        Assert.DoesNotContain(copies, Shown(vm));
        Assert.Equal(1L, await Decisions($"finding_key = '{copies.Key}' AND state = 'rejected' AND origin = 'human'"));
        Assert.Equal("7 Fundstücke · 6 offen · 1 abgelehnt · 6 gezeigt", vm.CountText);

        vm.SelectedStateFilter = FindingStateFilter.Refused;
        Assert.Equal([copies], Shown(vm));
        Select(vm, copies);
        await vm.ReopenCommand.ExecuteAsync(null);
        Assert.Equal(0L, await Decisions("1 = 1"));
        Assert.Empty(Shown(vm));
        Assert.Equal("Kein Fundstück passt zu den Filtern.", vm.EmptyText);
        vm.SelectedStateFilter = FindingStateFilter.Default;
        Assert.Contains(copies, Shown(vm));

        // "Falsch" says the finding itself does not hold: kept as a test case.
        Select(vm, copies);
        await vm.WrongCommand.ExecuteAsync(null);
        Assert.Equal(1L, await Decisions($"finding_key = '{copies.Key}' AND state = 'wrong'"));
        Assert.True(await _bed.ScalarAsync("SELECT COUNT(*) FROM eval_cases") > 0);
    }

    [AvaloniaFact]
    public async Task ChangeTarget_ExistingFolder_Refused_NewFolder_NoTarget_AndBack()
    {
        var vm = (await OpenAsync()).Findings;
        var key = Row(vm, "3 Bilder · Urlaub Kroatien 2021").Key;
        FindingRowViewModel Now() => vm.All.Single(r => r.Key == key);
        Select(vm, Now());

        // The root of the library is refused, the picker stays open with the reason; then an existing folder.
        var open = vm.ChangeTargetCommand.ExecuteAsync(null);
        await Until(() => vm.TargetPicker is { Roots.Count: 1 } p && p.Roots[0].Children.Any(c => c.Label == "Fotos"));
        var picker = vm.TargetPicker!;
        Assert.Equal("Ziel für 3 Bilder · Urlaub Kroatien 2021", picker.Title);
        Assert.Equal("Jetzt: umziehen → Fotos (laut Zielstruktur)", picker.CurrentText);
        picker.SelectedFolder = picker.Roots[0];
        await picker.AcceptAsync();
        Assert.Contains("outside_library", picker.ErrorText);
        Assert.True(vm.HasTargetPicker);
        picker.SelectedFolder = picker.Roots[0].Children.Single(c => c.Label == "Fotos");
        Assert.Equal("Neues Ziel: bibliothek/Fotos", picker.PreviewText);
        await picker.AcceptAsync();
        await open;
        Assert.False(vm.HasTargetPicker);
        Assert.Equal("umziehen → bibliothek/Fotos", Now().ActionText);
        Assert.Equal("1 Fundstück: Ziel bibliothek/Fotos.", vm.StatusText);
        Assert.True(Now().IsTargetDecided);
        Assert.Equal(FindingsViewModel.PlacedByHand, Now().Finding.Target!.Reason);
        // A target is no acceptance, and the selection stays on the finding.
        Assert.True(Now().IsOpen);
        Assert.Equal(key, Assert.IsType<FindingRowViewModel>(vm.SelectedRows.Single()).Key);

        // A new folder of the person's own name, with a reason.
        vm.Note = "eigener Ordner";
        open = vm.ChangeTargetCommand.ExecuteAsync(null);
        await Until(() => vm.TargetPicker is { Roots.Count: 1 } p && p.Roots[0].Children.Any(c => c.Label == "Fotos"));
        picker = vm.TargetPicker!;
        picker.SelectedFolder = picker.Roots[0].Children.Single(c => c.Label == "Fotos");
        picker.NewFolderName = "2021 Kroatien";
        picker.AddFolderCommand.Execute(null);
        await picker.AcceptAsync();
        await open;
        Assert.Equal("umziehen → bibliothek/Fotos/2021 Kroatien", Now().ActionText);
        Assert.Equal((FindingTargetStatus.NewFolder, "eigener Ordner"), (Now().Finding.TargetStatus!.Value, Now().Finding.Target!.Reason));

        // The inbox of the library.
        open = vm.ChangeTargetCommand.ExecuteAsync(null);
        await Until(() => vm.TargetPicker is { Roots.Count: 1 } p && p.Roots[0].Children.Any(c => c.IsInbox));
        vm.TargetPicker!.SelectedFolder = vm.TargetPicker.Roots[0].Children.Single(c => c.IsInbox);
        await vm.TargetPicker.AcceptAsync();
        await open;
        Assert.Equal(FindingTargetStatus.Inbox, Now().Finding.TargetStatus);

        await vm.NoTargetCommand.ExecuteAsync(null);
        Assert.Equal("bewusst ohne Ziel", Now().TargetText);
        Assert.True(vm.WithdrawTargetCommand.CanExecute(null));
        await vm.WithdrawTargetCommand.ExecuteAsync(null);
        Assert.Equal("Fotos (laut Zielstruktur)", Now().TargetText);
        Assert.False(vm.WithdrawTargetCommand.CanExecute(null));

        // Cancelled: nothing changes. A sensitive finding gets no target at all.
        open = vm.ChangeTargetCommand.ExecuteAsync(null);
        await Until(() => vm.HasTargetPicker);
        vm.TargetPicker!.Cancel();
        await open;
        Assert.Equal("Fotos (laut Zielstruktur)", Now().TargetText);
        Select(vm, Row(vm, "1 Datei · Privat"));
        Assert.False(vm.ChangeTargetCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task SeveralAtOnce_AndWholeBundles_LeaveSensitiveFindingsToASingleDecision()
    {
        var vm = (await OpenAsync()).Findings;
        Select(vm, [.. Shown(vm)]);
        Assert.StartsWith("7 Fundstücke ausgewählt, ", vm.SelectionText);

        await vm.AcceptCommand.ExecuteAsync(null);

        Assert.Equal("6 Fundstücke „angenommen“. 1 sensibles übersprungen: nur einzeln zu entscheiden.", vm.StatusText);
        var secret = Row(vm, "1 Datei · Privat");
        Assert.True(secret.IsOpen);
        Assert.Equal(6L, await Decisions("state = 'accepted'"));

        // Through its bundle it is not a single decision either.
        Select(vm, vm.Rows.OfType<FindingGroupViewModel>().Single(g => g.Title == "Privat"));
        await vm.AcceptCommand.ExecuteAsync(null);
        Assert.Equal("1 sensibles übersprungen: nur einzeln zu entscheiden.", vm.StatusText);
        Assert.True(secret.IsOpen);

        // As the one selected row it is.
        Select(vm, secret);
        await vm.AcceptCommand.ExecuteAsync(null);
        Assert.Equal(7L, await Decisions("state = 'accepted'"));

        // A bundle decides its findings.
        Select(vm, vm.Rows.OfType<FindingGroupViewModel>().Single(g => g.Title == "Alte Kopien"));
        Assert.Equal("2 Fundstücke ausgewählt, 3,7 MB", vm.SelectionText);
        await vm.RejectCommand.ExecuteAsync(null);
        Assert.Equal(2L, await Decisions("state = 'rejected'"));
        Assert.Equal("7 Fundstücke · 5 angenommen · 2 abgelehnt · 5 gezeigt", vm.CountText);

        // Closed, a bundle keeps its findings out of the list.
        var downloads = vm.Rows.OfType<FindingGroupViewModel>().Single(g => g.Key == "none");
        vm.ToggleGroupCommand.Execute(downloads);
        Assert.Equal(2, Shown(vm).Count);
        Assert.Equal("▸", vm.Rows.OfType<FindingGroupViewModel>().Single(g => g.Key == "none").ExpandGlyph);
    }

    [AvaloniaFact]
    public async Task Search_FindsByNamePathAndSummary_InFindingsAndFiles_AndEnds()
    {
        var main = await OpenAsync();
        var vm = main.Findings;

        // By name, typed: the search runs by itself shortly after. The words must all occur.
        main.SearchText = "rechnung pdf";
        await Until(() => main.SearchStatus == "1 Datei");
        Assert.Equal(["4 Dokumente · Downloads"], Shown(vm).Select(r => r.Title));
        Assert.Equal(["Rechnung_Strom.pdf"], main.FileTable.Rows.Select(r => r.Name));
        Assert.Equal("Downloads", main.FileTable.Rows.Single().FolderText);
        Assert.Equal("1 Datei in der Suche", main.FileTable.CountText);

        // By path: the folder of the photos, and the bundle of that name.
        main.SearchText = "kroatien";
        await main.SearchCommand.ExecuteAsync(null);
        Assert.Equal("3 Dateien", main.SearchStatus);
        Assert.Equal(["3 Bilder · Urlaub Kroatien 2021"], Shown(vm).Select(r => r.Title));

        // By summary, in the language of the model; the summary of access data is not searched.
        await _bed.SqlAsync("""
            UPDATE ai_results SET summary = 'Quartalsabrechnung des Versorgers'
             WHERE file_node_id = (SELECT id FROM file_nodes WHERE name = 'Rechnung_Strom.pdf');
            UPDATE ai_results SET summary = 'Quartalsabrechnung mit Zugang'
             WHERE file_node_id = (SELECT id FROM file_nodes WHERE name = 'passwoerter.txt');
            """);
        main.SearchText = "quartalsabrechnung";
        await main.SearchCommand.ExecuteAsync(null);
        Assert.Equal(["Rechnung_Strom.pdf"], main.FileTable.Rows.Select(r => r.Name));
        Assert.Equal(["4 Dokumente · Downloads"], Shown(vm).Select(r => r.Title));

        main.SearchText = "gibtesnicht";
        await main.SearchCommand.ExecuteAsync(null);
        Assert.Equal("Nichts passt zur Suche.", vm.EmptyText);
        Assert.Equal("Keine Datei passt zur Suche.", main.FileTable.EmptyText);

        main.ClearSearchCommand.Execute(null);
        Assert.Equal(7, Shown(vm).Count);
        Assert.Empty(main.SearchStatus);
        Assert.False(main.FileTable.IsSearch);

        // A folder chosen in the tree ends a search too; after a search the folder before it is back.
        await Until(() => main.FolderTree.Roots.SelectMany(r => r.Children).Any(c => c.DisplayName == "Downloads"));
        var downloads = main.FolderTree.Roots.SelectMany(r => r.Children).Single(c => c.DisplayName == "Downloads");
        main.FolderTree.SelectedItem = downloads;
        await Until(() => main.FileTable.Rows.Count == 8);
        main.SearchText = "kroatien";
        await main.SearchCommand.ExecuteAsync(null);
        Assert.Equal(3, main.FileTable.Rows.Count);
        main.ClearSearchCommand.Execute(null);
        await Until(() => main.FileTable.Rows.Count == 8 && main.Directory.Title == "Downloads");
        main.SearchText = "kroatien";
        await main.SearchCommand.ExecuteAsync(null);
        main.FolderTree.SelectedItem = downloads;
        await Until(() => main.FileTable.Rows.Count == 8);
        Assert.Empty(main.SearchText);
        Assert.Equal(7, Shown(vm).Count);
    }

    [AvaloniaFact]
    public async Task QuickFilters_CountAndFilter_KindsTogether_ReviewNarrows()
    {
        var main = await OpenAsync();
        var vm = main.Findings;
        FindingQuickFilterViewModel Filter(FindingQuickFilter f) => vm.QuickFilters.Single(q => q.Filter == f);
        Assert.Equal(["nicht gesichert", "sensibel", "prüfen", "doppelt"], vm.QuickFilters.Select(q => q.Label));
        Assert.Equal([2, 1, 1, 1], vm.QuickFilters.Select(q => q.Count));

        Filter(FindingQuickFilter.NotBackedUp).IsActive = true;
        Assert.Equal(2, Shown(vm).Count);
        Assert.All(Shown(vm), r => Assert.Equal(FindingKind.NotBackedUp, r.Finding.Kind));
        Assert.All(vm.Rows.OfType<FindingGroupViewModel>(), g => Assert.True(g.HasFindings));
        Assert.Equal("7 Fundstücke · 7 offen · 2 gezeigt", vm.CountText);

        Filter(FindingQuickFilter.Duplicate).IsActive = true;
        Filter(FindingQuickFilter.Sensitive).IsActive = true;
        Assert.Equal(4, Shown(vm).Count);

        Filter(FindingQuickFilter.Review).IsActive = true;
        Assert.Empty(Shown(vm));
        vm.ResetFiltersCommand.Execute(null);
        Filter(FindingQuickFilter.Review).IsActive = true;
        Assert.Equal(["1 Video · Downloads"], Shown(vm).Select(r => r.Title));
        Assert.Equal("unsicher", Shown(vm).Single().CertaintyLabel);

        // From the folder view a quick filter shows the findings.
        vm.ResetFiltersCommand.Execute(null);
        main.ShowFoldersCommand.Execute(null);
        Filter(FindingQuickFilter.Duplicate).IsActive = true;
        await Until(() => main.IsFindingsVisible && !vm.IsLoading);
        Assert.Equal(["doppelt"], Shown(vm).Select(r => r.KindLabel));
        Assert.False(vm.SelectedStateFilter.States.Contains(FindingState.Rejected));
    }

    [AvaloniaFact]
    public async Task Preview_StripDocumentAndFiles_SensitiveOnlyAfterClick_AccessDataNever()
    {
        var vm = (await OpenAsync()).Findings;

        // A bundle: picture strip and file list.
        Select(vm, vm.Rows.OfType<FindingGroupViewModel>().First());
        await vm.DetailLoad;
        Assert.Equal(("Urlaub Kroatien 2021", "Ordner"), (vm.DetailTitle, vm.DetailKind));
        Assert.Equal(3, vm.Pictures.Count);
        Assert.All(vm.Pictures, p => Assert.Equal(240, p.Image.PixelSize.Width));
        Assert.Equal(3, vm.FileItems.Count);
        Assert.Equal("3 Dateien:", vm.FileListTitle);
        Assert.Contains("nicht gesichert: 3 Bilder", vm.DetailAction);

        // Documents: the text of the first one.
        Select(vm, Row(vm, "4 Dokumente · Downloads"));
        await vm.DetailLoad;
        Assert.Empty(vm.Pictures);
        Assert.Equal("Einkaufsliste.txt", vm.DocumentTitle);
        Assert.Contains("Milch", vm.DocumentText);
        Assert.Equal(4, vm.FileItems.Count);
        Assert.Contains("worth keeping", vm.DetailReason);
        vm.OpenFileCommand.Execute(vm.FileItems[0].FullPath);
        Assert.Equal([vm.FileItems[0].FullPath], _bed.Shell.Opened);
        vm.RevealCommand.Execute(null);
        Assert.Equal([Path.Combine(_bed.Tree, "Downloads")], _bed.Shell.Revealed);

        // A marked file among other documents is listed, not shown.
        await _bed.SqlAsync("""
            UPDATE ai_results SET sensitive_markers = 'finance'
             WHERE file_node_id = (SELECT id FROM file_nodes WHERE name = 'Einkaufsliste.txt');
            """);
        Select(vm, Row(vm, "3 Bilder · Urlaub Kroatien 2021"));
        await vm.DetailLoad;
        Select(vm, Row(vm, "4 Dokumente · Downloads"));
        await vm.DetailLoad;
        Assert.DoesNotContain("Milch", vm.DocumentText);
        Assert.Equal("Finanzen", vm.FileItems.Single(f => f.RelativePath.EndsWith("Einkaufsliste.txt", StringComparison.Ordinal)).Marker);

        // Sensitive: nothing of it before the click, and of access data only the name after it.
        Select(vm, Row(vm, "1 Datei · Privat"));
        await vm.DetailLoad;
        Assert.True(vm.IsPreviewLocked);
        Assert.Empty(vm.FileItems);
        Assert.Contains("nur einzeln", vm.DetailState);
        await vm.ShowPreviewCommand.ExecuteAsync(null);
        Assert.False(vm.IsPreviewLocked);
        Assert.Equal("Privat/passwoerter.txt", vm.FileItems.Single().RelativePath);
        Assert.Equal(BrowserDisplay.AccessDataHint, vm.FileItems.Single().Summary);
        Assert.Equal(BrowserDisplay.AccessDataHint, vm.PreviewMessage);
        Assert.Empty(vm.DocumentText);

        // A sensitive finding that is no access data shows its document after the click.
        await _bed.SqlAsync("UPDATE ai_results SET sensitive_markers = NULL WHERE sensitive_markers = 'finance';");
        await _bed.SqlAsync("""
            UPDATE ai_results SET sensitive_markers = 'finance'
             WHERE file_node_id = (SELECT id FROM file_nodes WHERE name = 'Einkaufsliste.txt');
            """);
        await vm.ReloadCommand.ExecuteAsync(null);
        var finance = vm.All.Single(r => r.Finding.Kind == FindingKind.Sensitive && r.MarkersText == "Finanzen");
        Select(vm, finance);
        await vm.DetailLoad;
        Assert.True(vm.IsPreviewLocked);
        Assert.Empty(vm.DocumentText);
        await vm.ShowPreviewCommand.ExecuteAsync(null);
        Assert.Contains("Milch", vm.DocumentText);

        // Left and back: locked again only after another one was opened, the last opened one stays open.
        Select(vm, Row(vm, "1 Datei · Privat"));
        await vm.DetailLoad;
        Assert.True(vm.IsPreviewLocked);
    }

    [AvaloniaFact]
    public async Task Language_TranslatesTheCodes_WithoutLoadingAgain()
    {
        var vm = (await OpenAsync()).Findings;
        var row = Row(vm, "3 Bilder · Urlaub Kroatien 2021");
        Select(vm, row);
        var changed = 0;
        row.PropertyChanged += (_, _) => changed++;

        vm.SelectedLanguage = vm.Languages.Single(l => l.Language == DisplayLanguage.English);

        Assert.True(changed > 0);
        Assert.Equal(("not backed up", "open", "certain", "3 images · Urlaub Kroatien 2021"), (row.KindLabel, row.StateLabel, row.CertaintyLabel, row.Title));
        Assert.StartsWith("move → ", row.ActionText);
        Assert.Equal("not backed up", vm.DetailKind);
        Assert.Equal("7 Fundstücke · 7 open", vm.CountText);
        Assert.StartsWith("folder · ", vm.Rows.OfType<FindingGroupViewModel>().First().Summary);
    }

    [AvaloniaFact]
    public async Task JobBar_ShowsRoleAndStatus_AndSwitchesTheJob()
    {
        var main = await OpenAsync();
        await Until(() => main.JobBar.Items.Count == 2 && main.JobBar.Items.Any(i => i.HasAnalysis));
        var source = main.JobBar.Items.Single(i => i.Job.Id == _bed.JobId);
        var library = main.JobBar.Items.Single(i => i.Job.Id == _bed.LibraryJobId);
        Assert.Equal(("tree", "Quelle", "Scan fertig", true), (source.Name, source.RoleLabel, source.StatusLabel, source.IsActive));
        Assert.Equal(("bibliothek", "Bibliothek", false), (library.Name, library.RoleLabel, library.IsActive));
        Assert.Matches(@"^beurteilt \d+ von \d+$", source.AnalysisText);
        Assert.True(main.IsSidebarVisible);
        Assert.Same(source, main.JobBar.Selected);

        // A library has no findings to decide: its folders open.
        main.JobBar.Selected = library;
        await Until(() => main.IsBrowserVisible && main.FolderTree.Roots.SelectMany(r => r.Children).Any(c => c.DisplayName == "Fotos"));
        Assert.False(main.ShowFindingsCommand.CanExecute(null));
        Assert.Empty(main.Findings.Rows);
        Assert.False(main.IsSidebarVisible);

        main.ToggleSidebarCommand.Execute(null);
        Assert.True(main.IsSidebarVisible);
        main.JobBar.Selected = main.JobBar.Items.Single(i => i.Job.Id == _bed.JobId);
        await Until(() => main.IsFindingsVisible && main.Findings.Rows.Count > 0 && !main.Findings.IsLoading);
        Assert.True(main.JobBar.Items.Single(i => i.Job.Id == _bed.JobId).IsActive);
    }

    [AvaloniaFact]
    public async Task FolderTree_ShowsFileCounts_AndSortsBySizeAndCount()
    {
        var main = await OpenAsync();
        main.ShowFoldersCommand.Execute(null);
        var tree = main.FolderTree;
        await Until(() => tree.Roots.Count == 1 && tree.Roots[0].Children.Count == 4 && tree.Roots[0].Children.All(c => c.HasFacts));
        List<string> Names() => tree.Roots[0].Children.Select(c => c.DisplayName).ToList();
        Assert.Equal(["Alte Kopien", "Downloads", "Privat", "Urlaub Kroatien 2021"], Names());
        var downloads = tree.Roots[0].Children[1];
        Assert.Equal("8 Dateien", downloads.FilesText);
        var selections = 0;
        tree.SelectedItem = downloads;
        tree.DirectorySelected += (_, _) => selections++;

        tree.SelectedSort = FolderSort.Size;
        Assert.Equal(["Alte Kopien", "Downloads", "Urlaub Kroatien 2021", "Privat"], Names());
        tree.SelectedSort = FolderSort.Files;
        Assert.Equal(["Downloads", "Urlaub Kroatien 2021", "Alte Kopien", "Privat"], Names());
        tree.SelectedSort = FolderSort.Name;
        Assert.Equal(["Alte Kopien", "Downloads", "Privat", "Urlaub Kroatien 2021"], Names());
        Assert.Same(downloads, tree.SelectedItem);
        Assert.Equal(0, selections);
    }

    [AvaloniaFact]
    public async Task WithoutComparison_TheViewNamesTheMissingSteps_AndAnUndecidableChange()
    {
        await _bed.AnalyzeAsync();
        var main = Get<MainViewModel>();
        await main.OpenRecentScanCommand.ExecuteAsync(await Job(_bed.JobId));
        var vm = main.Findings;
        Assert.Contains("sfo compare", vm.MissingText);
        Assert.Contains("sfo bundles build", vm.MissingText);
        // No bundles: one list by importance.
        Assert.DoesNotContain(vm.Rows, r => r is FindingGroupViewModel);
        Assert.NotEmpty(vm.Rows);
        if (Shown(vm).FirstOrDefault(r => r.CanPlace) is not { } row) return;
        Select(vm, row);
        await vm.ChangeTargetCommand.ExecuteAsync(null);
        Assert.Contains("mit keiner Bibliothek verglichen", vm.StatusText);
        Assert.False(vm.HasTargetPicker);
    }

    [Fact]
    public void Spread_TakesPicturesOverTheWholeEntry()
    {
        var files = Enumerable.Range(0, 100).Select(i => new FileNode { Id = i }).ToList();
        Assert.Equal([0, 25, 50, 75], FindingsViewModel.Spread(files, 4).Select(f => f.Id));
        Assert.Equal(3, FindingsViewModel.Spread(files.Take(3).ToList(), 8).Count());
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
