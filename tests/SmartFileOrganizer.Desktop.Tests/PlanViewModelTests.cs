using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Desktop.Services;
using SmartFileOrganizer.Desktop.ViewModels;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Hosting;
using SmartFileOrganizer.Infrastructure.Content;

namespace SmartFileOrganizer.Desktop.Tests;

/// <summary>
/// The plan view model (https://github.com/crack666/smart_file_organizer/issues/15) on a plan an agent made and confirmed
/// through the CLI: read it, look into the files, set what happens to each file (Umziehen, In den Papierkorb, Hier lassen;
/// https://github.com/crack666/smart_file_organizer/issues/102), release, change targets, and run the two steps.
/// </summary>
public class PlanViewModelTests : IDisposable
{
    private readonly PlanTestbed _bed = new();

    public void Dispose()
    {
        _bed.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Open_ShowsThePlanOfTheJob_TheDatabase_AndWhatTheAgentConfirmed()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();

        Assert.Equal(Path.GetFullPath(_bed.DatabasePath), vm.DatabasePath);
        Assert.Contains(vm.DatabasePath, vm.DatabaseText);
        Assert.Equal(_bed.JobId, vm.SelectedJob!.Job.Id);
        Assert.Equal(_bed.PlanId, vm.SelectedPlan!.Plan.Id);
        Assert.NotEmpty(vm.Groups);
        // Bundles are the groups: the holiday folder is one action of its own bundle.
        Assert.Contains(vm.Groups, g => g.Actions.Any(r => r.Action.SourceKind == PlanSourceKind.Folder));
        var approved = vm.AllActions.Where(r => r.Action.State == PlanActionState.Approved).ToList();
        Assert.NotEmpty(approved);
        Assert.All(approved, r => Assert.True(r.DecidedByAgent));
        Assert.All(approved, r => Assert.Equal("Freigegeben", r.StateLabel));
        Assert.True(vm.HasAgentDecisions);
        // The sensitive action is never part of the agent's --all.
        var secret = vm.AllActions.Single(r => r.Action.Sensitive);
        Assert.Equal(PlanActionState.Proposed, secret.Action.State);
        Assert.Equal(PlanOutcome.Stay, secret.Outcome);
        Assert.StartsWith("Umziehen: ", vm.MoveText);
        Assert.StartsWith("Papierkorb: 0 ", vm.BinText);
        Assert.StartsWith("Hier lassen: 1 ", vm.StayText);
        Assert.Matches(@"^Freigegeben: \d+ von \d+$", vm.ReleasedText);
    }

    [Fact]
    public async Task ThreeOutcomes_OneByOne_SayWhatHappens_AndMustBeReleasedAgain()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        var invoice = Row(vm, "Rechnung_Strom.pdf");
        var target = invoice.Action.TargetPath!;
        Assert.Equal(PlanOutcome.Move, invoice.Outcome);
        Assert.Equal("→ bibliothek/Dokumente/Rechnung_Strom.pdf", invoice.TargetText);
        Assert.Contains("bibliothek/Dokumente/Rechnung_Strom.pdf", invoice.Consequence);
        Assert.Contains(PlanExecutor.HoldFolderName, invoice.Consequence);

        // In den Papierkorb: kind discard (#105); the original goes into the hold folder of the source, no copy anywhere.
        vm.SelectedRow = invoice;
        await vm.SetSelectedOutcomeCommand.ExecuteAsync(PlanOutcome.Bin);
        invoice = Row(vm, "Rechnung_Strom.pdf");
        Assert.Equal(PlanOutcome.Bin, invoice.Outcome);
        Assert.Equal(PlanActionKind.Discard, invoice.Action.Kind);
        Assert.Equal(PlanActionState.Proposed, invoice.Action.State);
        Assert.Equal("Offen", invoice.StateLabel);
        Assert.Equal("Papierkorb · ohne Kopie", invoice.TargetText);
        Assert.Equal("kein Ziel", invoice.ShortTargetText);
        Assert.Contains("Papierkorb", invoice.Consequence);
        Assert.Contains("keine Kopie", invoice.Consequence);
        Assert.Contains("„Papierkorb leeren“", invoice.Consequence);
        Assert.Contains("Noch nicht freigegeben", invoice.Consequence);
        await WaitUntil(() => vm.DetailConsequence == Row(vm, "Rechnung_Strom.pdf").Consequence);
        Assert.Single(vm.OutcomeChoices, c => c.IsCurrent && c.Outcome == PlanOutcome.Bin);
        Assert.StartsWith("Papierkorb: 1 ", vm.BinText);

        // Hier lassen: kind leave; nothing happens to the file.
        await vm.SetSelectedOutcomeCommand.ExecuteAsync(PlanOutcome.Stay);
        invoice = Row(vm, "Rechnung_Strom.pdf");
        Assert.Equal(PlanActionKind.Leave, invoice.Action.Kind);
        Assert.Equal("bleibt hier", invoice.TargetText);
        Assert.Contains("Beim Ausführen geschieht nichts", invoice.Consequence);

        // Umziehen again: out of the quarantine folder, back to the target it had.
        await vm.SetSelectedOutcomeCommand.ExecuteAsync(PlanOutcome.Move);
        invoice = Row(vm, "Rechnung_Strom.pdf");
        Assert.Equal(PlanActionKind.Move, invoice.Action.Kind);
        Assert.Equal(PlanChecks.Normalize(target), PlanChecks.Normalize(invoice.Action.TargetPath!));
        Assert.Equal(PlanActionOrigin.Human, invoice.Action.Origin);

        // The finer switch still reaches every kind of the data, through the same checks.
        vm.SelectedKind = vm.KindOptions.Single(o => o.Kind == PlanActionKind.Copy);
        await WaitUntil(() => Row(vm, "Rechnung_Strom.pdf").Action.Kind == PlanActionKind.Copy);
        Assert.Equal(PlanOutcome.Move, Row(vm, "Rechnung_Strom.pdf").Outcome);
        Assert.EndsWith("Original bleibt", Row(vm, "Rechnung_Strom.pdf").TargetText);
        await vm.SetSelectedKindAsync(PlanActionKind.DeleteSource);
        Assert.Equal(PlanActionKind.Copy, Row(vm, "Rechnung_Strom.pdf").Action.Kind);
        Assert.Contains("no_target", vm.StatusText);
        // Quarantine (a checked copy first) and delete_source stay reachable under "Genauer" next to discard (#105).
        Assert.Contains(vm.KindOptions, o => o.Kind == PlanActionKind.Quarantine);
        Assert.Contains(vm.KindOptions, o => o.Kind == PlanActionKind.DeleteSource);
        Assert.Contains(vm.KindOptions, o => o.Kind == PlanActionKind.Discard);
        await vm.SetSelectedKindAsync(PlanActionKind.Quarantine);
        Assert.Equal(PlanActionKind.Quarantine, Row(vm, "Rechnung_Strom.pdf").Action.Kind);
        Assert.StartsWith("Papierkorb · Kopie nach bibliothek/" + PlanOptions.DefaultQuarantineFolder, Row(vm, "Rechnung_Strom.pdf").TargetText);
    }

    [Fact]
    public async Task ThreeOutcomes_ForASelection_PassOverASensitiveAction()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        var photos = vm.AllActions.Where(r => r.Action.RelativePath.StartsWith("Downloads/IMG_", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, photos.Count);
        var targets = photos.ToDictionary(r => r.Id, r => r.Action.TargetPath!);
        foreach (var r in photos) r.IsChecked = true;
        vm.AllActions.Single(r => r.Action.Sensitive).IsChecked = true;

        await vm.BinCheckedCommand.ExecuteAsync(null);
        foreach (var id in targets.Keys)
            Assert.Equal(PlanActionKind.Discard, vm.AllActions.Single(r => r.Id == id).Action.Kind);
        Assert.StartsWith("Papierkorb: 2 ", vm.BinText);
        // A sensitive action is changed only on its own.
        Assert.Equal(PlanActionKind.Leave, vm.AllActions.Single(r => r.Action.Sensitive).Action.Kind);
        Assert.Contains("single_decision", vm.StatusText);
        // The ticks stay on the rows after the change.
        Assert.Equal(3, vm.CheckedCount);

        await vm.StayCheckedCommand.ExecuteAsync(null);
        Assert.All(targets.Keys, id => Assert.Equal(PlanOutcome.Stay, vm.AllActions.Single(r => r.Id == id).Outcome));

        await vm.MoveCheckedCommand.ExecuteAsync(null);
        foreach (var (id, target) in targets)
        {
            var a = vm.AllActions.Single(r => r.Id == id).Action;
            Assert.Equal(PlanActionKind.Move, a.Kind);
            Assert.Equal(PlanChecks.Normalize(target), PlanChecks.Normalize(a.TargetPath!));
        }

        // On its own it is the plan's checks that decide: a sensitive file never gets a copy in the quarantine folder ...
        vm.SelectedRow = vm.AllActions.Single(r => r.Action.Sensitive);
        await vm.SetSelectedKindAsync(PlanActionKind.Quarantine);
        Assert.Equal(PlanActionKind.Leave, vm.AllActions.Single(r => r.Action.Sensitive).Action.Kind);
        Assert.Contains("sensitive_kind", vm.StatusText);
        // ... but the person may put it, named on its own, into the bin of its source without a copy (#105).
        vm.SelectedRow = vm.AllActions.Single(r => r.Action.Sensitive);
        await vm.SetSelectedOutcomeCommand.ExecuteAsync(PlanOutcome.Bin);
        Assert.Equal(PlanActionKind.Discard, vm.AllActions.Single(r => r.Action.Sensitive).Action.Kind);
    }

    [Fact]
    public async Task Release_All_AGroup_ASelection_AndOneSensitiveAction_ThenTakeItBack()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();

        await vm.ResetAgentDecisionsCommand.ExecuteAsync(null);
        Assert.All(vm.AllActions, r => Assert.Equal(PlanActionState.Proposed, r.Action.State));
        Assert.False(vm.HasAgentDecisions);

        // A group: every action of the bundle.
        var holiday = vm.Groups.Single(g => g.Actions.Any(r => r.Action.SourceKind == PlanSourceKind.Folder));
        await vm.ReleaseGroupCommand.ExecuteAsync(holiday);
        Assert.All(GroupOf(vm, holiday.Key).Actions, r => Assert.True(r.IsReleased));
        Assert.Contains("freigegeben 1 von 1", GroupOf(vm, holiday.Key).Summary);

        // A selection of two.
        var photos = vm.AllActions.Where(r => r.Action.RelativePath.StartsWith("Downloads/IMG_", StringComparison.Ordinal)).ToList();
        foreach (var r in photos) r.IsChecked = true;
        await vm.ReleaseCheckedCommand.ExecuteAsync(null);
        Assert.All(photos, p => Assert.Equal(PlanActionState.Approved, vm.AllActions.Single(r => r.Id == p.Id).Action.State));
        Assert.All(photos, p => Assert.Equal(PlanActionOrigin.Human, vm.AllActions.Single(r => r.Id == p.Id).Action.DecidedBy));

        // Everything: a sensitive action is passed over and released only on its own.
        await ReleaseAllAsync(vm);
        Assert.Contains("single_decision", vm.StatusText);
        Assert.Equal(PlanActionState.Proposed, vm.AllActions.Single(r => r.Action.Sensitive).Action.State);
        Assert.All(vm.AllActions.Where(r => !r.Action.Sensitive), r => Assert.True(r.IsReleased));
        vm.SelectedRow = vm.AllActions.Single(r => r.Action.Sensitive);
        await WaitUntil(() => vm.DetailRelease.Contains("sensibel"));
        await vm.ReleaseSelectedCommand.ExecuteAsync(null);
        Assert.Equal(PlanActionState.Approved, vm.AllActions.Single(r => r.Action.Sensitive).Action.State);

        // Taken back for the ticked rows only.
        vm.ClearChecksCommand.Execute(null);
        foreach (var r in vm.AllActions.Where(r => photos.Any(p => p.Id == r.Id))) r.IsChecked = true;
        await vm.RevokeReleaseCommand.ExecuteAsync(null);
        Assert.All(photos, p => Assert.Equal(PlanActionState.Proposed, vm.AllActions.Single(r => r.Id == p.Id).Action.State));
        Assert.Equal(PlanActionState.Approved, Row(vm, "Rechnung_Strom.pdf").Action.State);
    }

    [Fact]
    public async Task Rejected_ShowsAsHierLassen_IsNeverReleased_AndAnOutcomeDecidesItAnew()
    {
        await _bed.CreatePlanAsync();
        var invoiceId = (await _bed.OpenPlanAsync()).AllActions.Single(r => Path.GetFileName(r.Action.SourcePath) == "Rechnung_Strom.pdf").Id;
        // Rejected outside the GUI, by an agent through the CLI; the data keep the state.
        await _bed.OkAsync("plan", "reject", _bed.PlanId.ToString(), "--action", invoiceId.ToString(), "--by", "agent");
        var vm = await _bed.OpenPlanAsync();

        var invoice = Row(vm, "Rechnung_Strom.pdf");
        Assert.Equal(PlanActionKind.Move, invoice.Action.Kind);
        Assert.Equal(PlanActionState.Rejected, invoice.Action.State);
        Assert.Equal(PlanOutcome.Stay, invoice.Outcome);
        Assert.Equal("Hier lassen", invoice.OutcomeLabel);
        Assert.Equal("Abgelehnt", invoice.StateLabel);
        Assert.True(invoice.DecidedByAgent);
        Assert.Equal("bleibt hier · abgelehnt", invoice.TargetText);
        Assert.StartsWith("Abgelehnt (Agent)", invoice.Consequence);
        Assert.Contains("Hier lassen: 2 ", vm.StayText);

        // A release never makes a rejected action move: not with everything, not on its own.
        await ReleaseAllAsync(vm);
        Assert.Equal(PlanActionState.Rejected, Row(vm, "Rechnung_Strom.pdf").Action.State);
        Assert.Contains("abgelehnt: 1", vm.StatusText);
        vm.SelectedRow = Row(vm, "Rechnung_Strom.pdf");
        await vm.ReleaseSelectedCommand.ExecuteAsync(null);
        Assert.Equal(PlanActionState.Rejected, Row(vm, "Rechnung_Strom.pdf").Action.State);
        Assert.Contains("erst einen Ausgang wählen", vm.StatusText);
        // "Hier lassen" is what it is already.
        await vm.SetSelectedOutcomeCommand.ExecuteAsync(PlanOutcome.Stay);
        Assert.Equal(PlanActionState.Rejected, Row(vm, "Rechnung_Strom.pdf").Action.State);

        // Umziehen decides it anew: open, kind and target as before, to be released.
        await vm.SetSelectedOutcomeCommand.ExecuteAsync(PlanOutcome.Move);
        invoice = Row(vm, "Rechnung_Strom.pdf");
        Assert.Equal(PlanActionState.Proposed, invoice.Action.State);
        Assert.Equal(PlanActionKind.Move, invoice.Action.Kind);
        Assert.Equal(PlanOutcome.Move, invoice.Outcome);
        await vm.ReleaseSelectedCommand.ExecuteAsync(null);
        Assert.Equal(PlanActionState.Approved, Row(vm, "Rechnung_Strom.pdf").Action.State);
    }

    [Fact]
    public async Task ChangeTarget_InTheFolderTreeOfTheLibrary_RefusedTarget_ThenANewFolder()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        // Something lies in the library under the invoice's name already (not scanned; the checks look at the disk).
        File.WriteAllText(Path.Combine(_bed.Library, "Fotos", "Rechnung_Strom.pdf"), "other");
        vm.SelectedRow = Row(vm, "Rechnung_Strom.pdf");

        var open = vm.ChangeTargetSelectedCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.TargetPicker is { Roots.Count: 1 } p && p.Roots[0].Children.Any(c => c.Label == "Fotos"));
        var picker = vm.TargetPicker!;
        var root = picker.Roots[0];
        Assert.Equal("bibliothek", root.Label);
        Assert.Equal("Rechnung_Strom.pdf", picker.Rows.Single().Name);
        Assert.Equal(Row(vm, "Rechnung_Strom.pdf").Action.Reason, picker.ReasonText);
        Assert.Equal("Jetzt: bibliothek/Dokumente/Rechnung_Strom.pdf", picker.CurrentText);
        // The inbox folder first, then the folders of the scan, read through the library's folder view.
        Assert.True(root.Children[0].IsInbox);
        Assert.Equal("_Inbox", root.Children[0].Label);
        Assert.Equal("Eingangsordner, wird beim Ausführen angelegt", root.Children[0].Note);
        var fotos = root.Children.Single(c => c.Label == "Fotos");
        fotos.IsExpanded = true;
        await WaitUntil(() => fotos.Children.Any(c => c.Label == "2019-06 Italien"));

        // Refused by the plan's checks: the picker stays open with the reason, nothing changes.
        picker.SelectedFolder = fotos;
        Assert.Equal("Neues Ziel: bibliothek/Fotos/Rechnung_Strom.pdf", picker.PreviewText);
        await picker.AcceptAsync();
        Assert.Contains("name_collision", picker.ErrorText);
        Assert.Contains("am Ziel liegt schon etwas", picker.ErrorText);
        Assert.Same(picker, vm.TargetPicker);
        Assert.EndsWith(Path.Combine("Dokumente", "Rechnung_Strom.pdf"), Row(vm, "Rechnung_Strom.pdf").Action.TargetPath);

        // A new folder below the root: allowed; the action is open again.
        picker.SelectedFolder = root;
        picker.NewFolderName = "a/b";
        picker.AddFolderCommand.Execute(null);
        Assert.Contains("kein Ordnername", picker.ErrorText);
        picker.NewFolderName = "Rechnungen";
        picker.AddFolderCommand.Execute(null);
        Assert.True(picker.SelectedFolder!.IsNew);
        await picker.AcceptAsync();
        await open;
        Assert.Null(vm.TargetPicker);
        var invoice = Row(vm, "Rechnung_Strom.pdf");
        Assert.Equal(PlanChecks.Normalize(Path.Combine(_bed.Library, "Rechnungen", "Rechnung_Strom.pdf")), PlanChecks.Normalize(invoice.Action.TargetPath!));
        Assert.Equal(PlanActionState.Proposed, invoice.Action.State);
        Assert.Contains("neu freizugeben", vm.StatusText);
    }

    [Fact]
    public async Task ChangeTarget_ForASelection_MakesLeaveMove_AndRefusesAPlaceOutsideTheLibraries()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        var photos = vm.AllActions.Where(r => r.Action.RelativePath.StartsWith("Downloads/IMG_", StringComparison.Ordinal)).ToList();
        vm.SelectedRow = photos[0];
        await vm.SetSelectedOutcomeCommand.ExecuteAsync(PlanOutcome.Stay);
        var ids = photos.Select(p => p.Id).ToList();

        // Outside every library (here: the source itself): refused, nothing changes.
        var refused = await vm.SetTargetAsync(ids, Path.Combine(_bed.Tree, "Downloads"));
        Assert.NotNull(refused);
        Assert.Contains("target_outside_library", refused);
        Assert.Equal(PlanActionKind.Leave, vm.AllActions.Single(r => r.Id == ids[0]).Action.Kind);

        var folder = Path.Combine(_bed.Library, "Fotos", "2023-08");
        Assert.Null(await vm.SetTargetAsync(ids, folder));
        foreach (var p in photos)
        {
            var a = vm.AllActions.Single(r => r.Id == p.Id).Action;
            Assert.Equal(PlanActionKind.Move, a.Kind);
            Assert.Equal(PlanChecks.Normalize(Path.Combine(folder, Path.GetFileName(a.SourcePath))), PlanChecks.Normalize(a.TargetPath!));
        }
    }

    [Fact]
    public async Task Detail_ShowsTheFile_OpensIt_AndKeepsAccessDataText()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();

        var list = Row(vm, "Einkaufsliste.txt");
        vm.SelectedRow = list;
        await WaitUntil(() => vm.HasTextPreview);
        Assert.Contains("Einkaufsliste", vm.TextPreview);
        Assert.Contains("Document", vm.DetailFacts);
        Assert.Equal(list.Action.Reason, vm.DetailReason);
        Assert.Equal(PlanActionKind.Move, vm.SelectedKind!.Kind);
        Assert.Equal(list.Consequence, vm.DetailConsequence);
        Assert.Equal("Freigegeben", vm.DetailRelease);
        Assert.False(vm.HasMedia);

        vm.OpenSelectedCommand.Execute(null);
        vm.RevealSelectedCommand.Execute(null);
        Assert.Equal([list.Action.SourcePath], _bed.Shell.Opened);
        Assert.Equal([list.Action.SourcePath], _bed.Shell.Revealed);

        vm.SelectedRow = vm.AllActions.Single(r => r.Action.Sensitive);
        await WaitUntil(() => vm.HasPreviewMessage);
        Assert.False(vm.HasTextPreview);
        Assert.Contains("Zugangsdaten", vm.PreviewMessage);
        Assert.Contains("credentials", vm.DetailMarker);
        Assert.DoesNotContain("dummy-not-real", vm.DetailFacts + vm.DetailDescription + vm.TextPreview);

        // A folder action shows its files.
        vm.SelectedRow = vm.AllActions.Single(r => r.Action.SourceKind == PlanSourceKind.Folder);
        await WaitUntil(() => vm.DetailFacts.Contains("IMG_20210703_100000.jpg"));
    }

    [Fact]
    public void MediaLine_GivesTheKeyFactsOfVideoAndAudio_InOneLine()
    {
        var video = new FileNode { Name = "clip.mp4", FileType = FileType.Video, Size = 3 * 1024 * 1024 };
        Assert.Equal($"Video · 1:05 · 1920 × 1080 · Phone X · 2021-07-03 · {PlanDisplay.Size(video.Size)}", PlanViewModel.MediaLine(video,
            [new(FileFactKey.DurationSeconds, "64.6"), new(FileFactKey.Width, "1920"), new(FileFactKey.Height, "1080"),
             new(FileFactKey.Device, "Phone X"), new(FileFactKey.TakenAt, "2021-07-03T10:00:00")], showsFreeText: true));
        var audio = new FileNode { Name = "song.mp3", FileType = FileType.Audio, Size = 2048 };
        Assert.Equal($"Audio · 3:12 · Some Band · A Title · {PlanDisplay.Size(audio.Size)}", PlanViewModel.MediaLine(audio,
            [new(FileFactKey.DurationSeconds, "192"), new(FileFactKey.Artist, "Some Band"), new(FileFactKey.Title, "A Title")], showsFreeText: true));
        Assert.Equal(string.Empty, PlanViewModel.MediaLine(new FileNode { Name = "a.txt", FileType = FileType.Document }, [], true));
    }

    /// <summary>
    /// A bitmap needs the Avalonia platform: headless, as the render test. Without ffmpeg the preview says so and offers
    /// "Abspielen"; with it (on this machine, possibly not on a CI runner) a short video gets one picture per second.
    /// Since #109 a filmstrip; until 16.11.2026 one still frame.
    /// </summary>
    [AvaloniaFact]
    public async Task VideoPreview_IsAFilmstrip_WhenFfmpegIsThere_ElseSaysWhyNot()
    {
        var file = Path.Combine(_bed.Root, "clip.mp4");
        Directory.CreateDirectory(_bed.Root);
        var node = new FileNode { Name = "clip.mp4", FullPath = file, FileType = FileType.Video, Extension = ".mp4" };
        var cache = Path.Combine(_bed.Root, "cache");

        var none = new FilePreviewService(new FileContentExtractor(), new VideoFrameExtractor(() => null), cache);
        File.WriteAllBytes(file, [0, 0, 0, 24]);
        var without = await none.BuildAsync(node);
        Assert.Empty(without.Frames);
        Assert.True(without.CanPlay);
        Assert.StartsWith("Kein Bildstreifen: ffmpeg ist auf diesem Rechner nicht gefunden", without.Message);

        var frames = new VideoFrameExtractor();
        if (!frames.IsAvailable) return; // the pictures themselves need ffmpeg on this machine
        var psi = new ProcessStartInfo(VideoFrameExtractor.ToolName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10:duration=2", "-pix_fmt", "yuv420p", file })
            psi.ArgumentList.Add(arg);
        using (var make = Process.Start(psi))
        {
            await make!.WaitForExitAsync();
            Assert.Equal(0, make.ExitCode);
        }
        var with = await new FilePreviewService(new FileContentExtractor(), frames, cache).BuildAsync(node);
        Assert.True(with.Frames.Count == 2, with.Message);
        Assert.Equal(["0:00", "0:01"], with.Frames.Select(f => f.Label));
        with.Dispose();
    }

    [Fact]
    public void FilmstripPositions_AreTheMiddlesOfEqualParts_AtMostNine_OnePerSecondWhenShort()
    {
        Assert.Equal([10, 30, 50, 70, 90, 110, 130, 150, 170], FilePreviewService.FilmstripPositions(180));
        Assert.Equal([0.5, 1.5, 2.5], FilePreviewService.FilmstripPositions(3));
        Assert.Equal([0.2], FilePreviewService.FilmstripPositions(0.4));
        Assert.Equal([0], FilePreviewService.FilmstripPositions(0));
    }

    [Fact]
    public async Task DryRun_ChangesNothing_AndSummarisesWhatWouldRun()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        var tree = Snapshot(_bed.Tree);
        var library = Snapshot(_bed.Library);

        await vm.DryRunCommand.ExecuteAsync(null);

        Assert.False(vm.LastReport!.Executed);
        Assert.Contains(vm.LastReport.Items, i => i.Outcome == "would_run");
        Assert.Contains("würden laufen", vm.RunSummary);
        Assert.Contains("Kollisionen 0", vm.RunSummary);
        Assert.Equal(tree, Snapshot(_bed.Tree));
        Assert.Equal(library, Snapshot(_bed.Library));
    }

    [Fact]
    public async Task BeforeExecuting_TheSummarySaysWhatMovesWhere_WhatGoesIntoTheBin_AndWhatStays()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        var photos = vm.AllActions.Where(r => r.Action.RelativePath.StartsWith("Downloads/IMG_", StringComparison.Ordinal)).ToList();
        foreach (var r in photos) r.IsChecked = true;
        await vm.BinCheckedCommand.ExecuteAsync(null);
        await vm.ReleaseCheckedCommand.ExecuteAsync(null);
        vm.SelectedRow = Row(vm, "Einkaufsliste.txt");
        await vm.SetSelectedOutcomeCommand.ExecuteAsync(PlanOutcome.Stay);

        var step1 = vm.ApplyCommand.ExecuteAsync(null);
        var confirm = await ConfirmationAsync(vm, step1);
        var text = string.Join("\n", confirm.Lines);
        Assert.Contains("Umziehen: 2 Aktionen", text);
        Assert.Contains("→ bibliothek/Fotos: 1 Aktion", text);
        Assert.Contains("→ bibliothek/Dokumente: 1 Aktion", text);
        Assert.Contains("In den Papierkorb: 2 Aktionen", text);
        Assert.Contains("tree/" + PlanExecutor.HoldFolderName, text);
        Assert.Contains("sfo plan purge", text);
        Assert.Contains("Bleiben, wo sie sind: 2 Aktionen", text);
        Assert.Contains("2 hier lassen", text);
        Assert.Contains("Kein Original", text);
        confirm.CancelCommand.Execute(null);
        await step1;
    }

    [Fact]
    public async Task Execute_InTwoConfirmedSteps_ThenUndo_LeavesTheTreesAsTheyWere()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        var tree = Snapshot(_bed.Tree);
        var library = Snapshot(_bed.Library);

        // Declined: nothing happens.
        var declined = vm.ApplyCommand.ExecuteAsync(null);
        (await ConfirmationAsync(vm, declined)).CancelCommand.Execute(null);
        await declined;
        Assert.Equal(library, Snapshot(_bed.Library));

        // Step 1: copies, checked; no original is touched.
        var step1 = vm.ApplyCommand.ExecuteAsync(null);
        var confirm1 = await ConfirmationAsync(vm, step1);
        Assert.False(confirm1.IsDanger);
        Assert.Contains(confirm1.Lines, l => l.Contains("Kein Original"));
        confirm1.AcceptCommand.Execute(null);
        await step1;
        Assert.True(vm.LastReport!.Executed);
        Assert.All(vm.LastReport.Items, i => Assert.Equal("done", i.Outcome));
        Assert.Equal(tree, Snapshot(_bed.Tree));
        Assert.NotEqual(library, Snapshot(_bed.Library));
        Assert.Contains(vm.AllActions, r => r.Action.State == PlanActionState.Verified);
        Assert.False(vm.IsRunning);

        // Step 2: its own confirmation, with what goes into the bin; room is freed only by emptying it (#105).
        var step2 = vm.RemoveSourcesCommand.ExecuteAsync(null);
        var confirm2 = await ConfirmationAsync(vm, step2);
        Assert.True(confirm2.IsDanger);
        Assert.Contains(confirm2.Lines, l => l.Contains(PlanExecutor.HoldFolderName));
        Assert.Contains(confirm2.Lines, l => l.StartsWith("In den Papierkorb kommen", StringComparison.Ordinal)
                                             && l.Contains("erst mit „Papierkorb leeren“ frei"));
        confirm2.AcceptCommand.Execute(null);
        await step2;
        Assert.False(File.Exists(Path.Combine(_bed.Tree, "Downloads", "Rechnung_Strom.pdf")));
        Assert.True(Directory.Exists(Path.Combine(_bed.Tree, PlanExecutor.HoldFolderName)));
        Assert.Contains(vm.AllActions, r => r.Action.State == PlanActionState.Executed);
        // After the run the lines say what happened, not what will.
        Assert.StartsWith("Ausgeführt: das Original liegt im Papierkorb", Row(vm, "Rechnung_Strom.pdf").Consequence);

        // Undo puts everything back.
        var undo = vm.UndoCommand.ExecuteAsync(null);
        (await ConfirmationAsync(vm, undo)).AcceptCommand.Execute(null);
        await undo;
        Assert.Equal(tree, Snapshot(_bed.Tree));
        Assert.Equal(library, Snapshot(_bed.Library));
        Assert.Contains(vm.AllActions, r => r.Action.State == PlanActionState.Undone);
    }

    [Fact]
    public async Task InDenPapierkorb_HoldsWithoutACopy_AndOnlyEmptyingTheBinAfterAConfirmationDeletes()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        var source = Path.Combine(_bed.Tree, "Downloads", "Rechnung_Strom.pdf");
        var size = new FileInfo(source).Length;
        vm.SelectedRow = Row(vm, "Rechnung_Strom.pdf");
        await vm.SetSelectedOutcomeCommand.ExecuteAsync(PlanOutcome.Bin);
        Assert.Equal(PlanActionKind.Discard, Row(vm, "Rechnung_Strom.pdf").Action.Kind);
        vm.SelectedRow = Row(vm, "Rechnung_Strom.pdf");
        await vm.ReleaseSelectedCommand.ExecuteAsync(null);
        Assert.Equal(PlanActionState.Approved, Row(vm, "Rechnung_Strom.pdf").Action.State);
        Assert.False(vm.HasBinHeld);

        var step1 = vm.ApplyCommand.ExecuteAsync(null);
        (await ConfirmationAsync(vm, step1)).AcceptCommand.Execute(null);
        await step1;
        var library = Snapshot(_bed.Library);
        Assert.DoesNotContain(library.Keys, k => k.EndsWith("Rechnung_Strom.pdf", StringComparison.Ordinal));
        Assert.Equal(PlanActionState.Verified, Row(vm, "Rechnung_Strom.pdf").Action.State);
        Assert.Equal("Geprüft", Row(vm, "Rechnung_Strom.pdf").StateLabel);
        Assert.True(File.Exists(source));

        var step2 = vm.RemoveSourcesCommand.ExecuteAsync(null);
        (await ConfirmationAsync(vm, step2)).AcceptCommand.Execute(null);
        await step2;
        Assert.False(File.Exists(source));
        Assert.Equal(library, Snapshot(_bed.Library));
        var hold = Path.Combine(_bed.Tree, PlanExecutor.HoldFolderName);
        Assert.Contains(Directory.GetFiles(hold, "*", SearchOption.AllDirectories), f => Path.GetFileName(f) == "Rechnung_Strom.pdf");
        await WaitUntil(() => vm.HasBinHeld);
        Assert.StartsWith("Im Papierkorb: ", vm.BinHeldText);
        Assert.StartsWith("Ausgeführt", Row(vm, "Rechnung_Strom.pdf").Consequence);

        // Emptying asks first, with count and size; declined, nothing is deleted.
        var declined = vm.EmptyBinCommand.ExecuteAsync(null);
        var ask = await ConfirmationAsync(vm, declined);
        Assert.True(ask.IsDanger);
        Assert.Contains(ask.Lines, l => l.StartsWith("Endgültig gelöscht werden ", StringComparison.Ordinal)
                                        && l.Contains("nicht zurücknehmen"));
        Assert.Contains(ask.Lines, l => l.Contains("ohne Kopie") && l.Contains(PlanDisplay.Size(size)) && l.Contains("nirgends mehr"));
        Assert.Contains(ask.Lines, l => l.StartsWith("Frei wird: ", StringComparison.Ordinal) && l.Contains("Laufwerk der Quelle"));
        ask.CancelCommand.Execute(null);
        await declined;
        Assert.Contains(Directory.GetFiles(hold, "*", SearchOption.AllDirectories), f => Path.GetFileName(f) == "Rechnung_Strom.pdf");
        Assert.True(vm.HasBinHeld);

        // Confirmed: the bin is empty, the file is gone for good and can no longer be put back.
        var purge = vm.EmptyBinCommand.ExecuteAsync(null);
        (await ConfirmationAsync(vm, purge)).AcceptCommand.Execute(null);
        await purge;
        Assert.False(Directory.Exists(hold) && Directory.GetFiles(hold, "*", SearchOption.AllDirectories).Length > 0);
        Assert.False(File.Exists(source));
        Assert.Equal(library, Snapshot(_bed.Library));
        await WaitUntil(() => !vm.HasBinHeld);
    }

    [Fact]
    public async Task AStoppedRun_ShowsItsProgress_AndTheNextRunFinishesIt()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        var tree = Snapshot(_bed.Tree);
        var copies = 0;
        var progressSeen = string.Empty;
        _bed.Hook = (point, _) =>
        {
            if (point != PlanExecutionHooks.Copied) return;
            if (++copies == 3)
            {
                progressSeen = vm.RunProgressText;
                vm.CancelRunCommand.Execute(null);
            }
        };

        var stopped = vm.ApplyCommand.ExecuteAsync(null);
        (await ConfirmationAsync(vm, stopped)).AcceptCommand.Execute(null);
        await stopped;
        Assert.Contains("abgebrochen", vm.StatusText);
        Assert.False(vm.IsRunning);
        Assert.Matches(@"^\d+ / \d+", progressSeen);
        Assert.Equal(tree, Snapshot(_bed.Tree));

        _bed.Hook = null;
        var again = vm.ApplyCommand.ExecuteAsync(null);
        (await ConfirmationAsync(vm, again)).AcceptCommand.Execute(null);
        await again;
        Assert.Contains("fertig", vm.StatusText);
        Assert.All(vm.AllActions.Where(r => r.Action.Kind == PlanActionKind.Move),
            r => Assert.Equal(PlanActionState.Verified, r.Action.State));
        Assert.Empty(Directory.GetFiles(_bed.Library, "*" + PlanExecutor.PartialSuffix, SearchOption.AllDirectories));
        Assert.Equal(100, vm.RunProgress);
    }

    [Fact]
    public async Task KeepSource_IsTheSwitchOfTheJob_AndTheLinesSayTheOriginalStays()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        Assert.False(vm.KeepSource);
        vm.KeepSource = true;
        var executor = _bed.GuiServices().GetRequiredService<PlanExecutor>();
        await WaitUntil(() => executor.GetKeepSourceAsync(_bed.JobId).GetAwaiter().GetResult());
        await WaitUntil(() => Row(vm, "Rechnung_Strom.pdf").TargetText.EndsWith("Original bleibt", StringComparison.Ordinal));
        Assert.Contains("Quelle nicht anfassen", Row(vm, "Rechnung_Strom.pdf").Consequence);
    }

    [Fact]
    public async Task TheGui_OpensTheDatabaseOfSfoDb_AndSettingsOfSfoSettings()
    {
        // Only this test reads the variables; every other one names its database itself (options and --db win).
        var db = Path.Combine(_bed.Root, "env", "data.db");
        var settings = Path.Combine(_bed.Root, "env", "gui-settings.json");
        var oldDb = Environment.GetEnvironmentVariable(SmartFileOrganizerHostOptions.DatabasePathVariable);
        var oldSettings = Environment.GetEnvironmentVariable(SmartFileOrganizerHostOptions.SettingsPathVariable);
        try
        {
            Environment.SetEnvironmentVariable(SmartFileOrganizerHostOptions.DatabasePathVariable, db);
            Environment.SetEnvironmentVariable(SmartFileOrganizerHostOptions.SettingsPathVariable, settings);
            var services = ServiceConfigurator.Build(configure: null, overrides: null);
            var paths = services.GetRequiredService<SmartFileOrganizerPaths>();
            Assert.Equal(Path.GetFullPath(db), paths.DatabasePath);
            Assert.Equal(Path.GetFullPath(settings), paths.SettingsPath);
            Assert.True(File.Exists(db));
            Assert.Equal(Path.GetFullPath(db), services.GetRequiredService<PlanViewModel>().DatabasePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SmartFileOrganizerHostOptions.DatabasePathVariable, oldDb);
            Environment.SetEnvironmentVariable(SmartFileOrganizerHostOptions.SettingsPathVariable, oldSettings);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static PlanActionRowViewModel Row(PlanViewModel vm, string name) =>
        vm.AllActions.Single(r => Path.GetFileName(r.Action.SourcePath) == name);

    private static PlanGroupViewModel GroupOf(PlanViewModel vm, string key) => vm.Groups.Single(g => g.Key == key);

    private static async Task<PlanConfirmationViewModel> ConfirmationAsync(PlanViewModel vm, Task running)
    {
        await WaitUntil(() => vm.Confirmation is not null || running.IsCompleted);
        if (running.IsCompleted) await running;
        return vm.Confirmation ?? throw new InvalidOperationException("No confirmation was asked for: " + vm.StatusText);
    }

    /// <summary>"Alles freigeben" and yes to its confirmation (since #109 it asks first).</summary>
    private static async Task ReleaseAllAsync(PlanViewModel vm)
    {
        var running = vm.ReleaseAllCommand.ExecuteAsync(null);
        (await ConfirmationAsync(vm, running)).AcceptCommand.Execute(null);
        await running;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "condition not met in time");
    }

    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            result[Path.GetRelativePath(root, dir) + "/"] = "folder";
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            result[Path.GetRelativePath(root, file)] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        return result;
    }
}
