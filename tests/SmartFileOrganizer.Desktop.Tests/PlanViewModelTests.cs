using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Desktop.ViewModels;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Hosting;

namespace SmartFileOrganizer.Desktop.Tests;

/// <summary>
/// The plan view model (https://github.com/crack666/smart_file_organizer/issues/15) on a plan an agent made and confirmed
/// through the CLI: read it, look into the files, confirm, reject, change kinds, mark clutter, and run the two steps.
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
        Assert.True(vm.HasAgentDecisions);
        // The sensitive action is never part of the agent's --all.
        var secret = vm.AllActions.Single(r => r.Action.Sensitive);
        Assert.Equal(PlanActionState.Proposed, secret.Action.State);
        Assert.StartsWith("Zieht um: ", vm.MoveText);
    }

    [Fact]
    public async Task ResetAgentDecisions_ThenConfirmAndReject_OneByOne_PerGroup_AndAll()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();

        await vm.ResetAgentDecisionsCommand.ExecuteAsync(null);
        Assert.All(vm.AllActions, r => Assert.Equal(PlanActionState.Proposed, r.Action.State));
        Assert.False(vm.HasAgentDecisions);

        // One by one: the person confirms it.
        var invoice = Row(vm, "Rechnung_Strom.pdf");
        vm.SelectedRow = invoice;
        await vm.ApproveSelectedCommand.ExecuteAsync(null);
        invoice = Row(vm, "Rechnung_Strom.pdf");
        Assert.Equal(PlanActionState.Approved, invoice.Action.State);
        Assert.Equal(PlanActionOrigin.Human, invoice.Action.DecidedBy);
        Assert.False(invoice.DecidedByAgent);

        // A group: every action of the bundle, but a sensitive one only on its own.
        var privat = vm.Groups.Single(g => g.Actions.Any(r => r.Action.Sensitive));
        await vm.ApproveGroupCommand.ExecuteAsync(privat);
        Assert.Equal(PlanActionState.Proposed, vm.AllActions.Single(r => r.Action.Sensitive).Action.State);
        Assert.Contains("single_decision", vm.StatusText);
        vm.SelectedRow = vm.AllActions.Single(r => r.Action.Sensitive);
        await vm.ApproveSelectedCommand.ExecuteAsync(null);
        Assert.Equal(PlanActionState.Approved, vm.AllActions.Single(r => r.Action.Sensitive).Action.State);

        var holiday = vm.Groups.Single(g => g.Actions.Any(r => r.Action.SourceKind == PlanSourceKind.Folder));
        await vm.RejectGroupCommand.ExecuteAsync(holiday);
        Assert.All(GroupOf(vm, holiday.Key).Actions, r => Assert.Equal(PlanActionState.Rejected, r.Action.State));
        Assert.Contains("Bleibt: ", vm.StayText);

        // Ticked rows: two at once are a selection.
        foreach (var r in vm.AllActions.Where(r => r.Action.State == PlanActionState.Rejected)) r.IsChecked = true;
        Assert.True(vm.CheckedCount > 0);
        await vm.ApproveCheckedCommand.ExecuteAsync(null);
        Assert.All(GroupOf(vm, holiday.Key).Actions, r => Assert.Equal(PlanActionState.Approved, r.Action.State));

        await vm.RejectAllCommand.ExecuteAsync(null);
        Assert.All(vm.AllActions, r => Assert.Equal(PlanActionState.Rejected, r.Action.State));
        await vm.ApproveAllCommand.ExecuteAsync(null);
        // --all passes over the sensitive action as the CLI does.
        Assert.Equal(PlanActionState.Rejected, vm.AllActions.Single(r => r.Action.Sensitive).Action.State);
        Assert.Contains(vm.AllActions, r => r.Action.State == PlanActionState.Approved);
    }

    [Fact]
    public async Task ChangeKind_IsCheckedByThePlan_AndMustBeConfirmedAgain()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        var invoice = Row(vm, "Rechnung_Strom.pdf");
        var target = invoice.Action.TargetPath!;
        Assert.Equal(PlanActionKind.Move, invoice.Action.Kind);
        Assert.Equal(PlanActionState.Approved, invoice.Action.State);

        vm.SelectedRow = invoice;
        vm.SelectedKind = vm.KindOptions.Single(o => o.Kind == PlanActionKind.Copy);
        await WaitUntil(() => Row(vm, "Rechnung_Strom.pdf").Action.Kind == PlanActionKind.Copy);
        invoice = Row(vm, "Rechnung_Strom.pdf");
        Assert.Equal(PlanActionState.Proposed, invoice.Action.State);
        Assert.Equal(PlanActionOrigin.Human, invoice.Action.Origin);
        Assert.Equal(target, invoice.Action.TargetPath);

        // delete_source needs a library that holds the content; the plan refuses it and nothing changes.
        await vm.SetSelectedKindAsync(PlanActionKind.DeleteSource);
        Assert.Equal(PlanActionKind.Copy, Row(vm, "Rechnung_Strom.pdf").Action.Kind);
        Assert.Contains("no_target", vm.StatusText);

        await vm.LeaveSelectedCommand.ExecuteAsync(null);
        Assert.Equal(PlanActionKind.Leave, Row(vm, "Rechnung_Strom.pdf").Action.Kind);
    }

    [Fact]
    public async Task MarkAsClutter_GoesToQuarantine_ShowsInTheSummary_AndComesBackOut()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        var photos = vm.AllActions.Where(r => r.Action.RelativePath.StartsWith("Downloads/IMG_", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, photos.Count);
        var targets = photos.ToDictionary(r => r.Id, r => r.Action.TargetPath!);
        var removeBefore = vm.RemoveText;

        foreach (var r in photos) r.IsChecked = true;
        await vm.MarkCheckedForRemovalCommand.ExecuteAsync(null);

        var quarantine = PlanChecks.Join(_bed.Library, PlanOptions.DefaultQuarantineFolder);
        foreach (var id in targets.Keys)
        {
            var a = vm.AllActions.Single(r => r.Id == id).Action;
            Assert.Equal(PlanActionKind.Quarantine, a.Kind);
            Assert.Equal(PlanActionState.Proposed, a.State);
            Assert.True(PlanChecks.IsAtOrBelow(a.TargetPath!, quarantine), a.TargetPath);
        }
        Assert.NotEqual(removeBefore, vm.RemoveText);
        Assert.StartsWith("Wird entfernt: 2 ", vm.RemoveText);
        // The ticks stay on the rows after the change.
        Assert.Equal(2, vm.CheckedCount);

        await vm.MoveCheckedCommand.ExecuteAsync(null);
        foreach (var (id, target) in targets)
        {
            var a = vm.AllActions.Single(r => r.Id == id).Action;
            Assert.Equal(PlanActionKind.Move, a.Kind);
            Assert.Equal(PlanChecks.Normalize(target), PlanChecks.Normalize(a.TargetPath!));
        }
    }

    [Fact]
    public async Task MarkAsClutter_NeverTakesASensitiveActionAlongInASelection()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        var secret = vm.AllActions.Single(r => r.Action.Sensitive);
        var photo = vm.AllActions.First(r => r.Action.RelativePath.StartsWith("Downloads/IMG_", StringComparison.Ordinal));
        secret.IsChecked = true;
        photo.IsChecked = true;

        await vm.MarkCheckedForRemovalCommand.ExecuteAsync(null);

        Assert.Equal(PlanActionKind.Leave, vm.AllActions.Single(r => r.Action.Sensitive).Action.Kind);
        Assert.Equal(PlanActionKind.Quarantine, vm.AllActions.Single(r => r.Id == photo.Id).Action.Kind);
        Assert.Contains("single_decision", vm.StatusText);

        // On its own it is the plan's checks that decide: a sensitive file never goes to quarantine.
        vm.SelectedRow = vm.AllActions.Single(r => r.Action.Sensitive);
        await vm.MarkSelectedForRemovalCommand.ExecuteAsync(null);
        Assert.Equal(PlanActionKind.Leave, vm.AllActions.Single(r => r.Action.Sensitive).Action.Kind);
        Assert.Contains("sensitive_kind", vm.StatusText);
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

        // Step 2: its own confirmation, with what goes and the room it frees.
        var step2 = vm.RemoveSourcesCommand.ExecuteAsync(null);
        var confirm2 = await ConfirmationAsync(vm, step2);
        Assert.True(confirm2.IsDanger);
        Assert.Contains(confirm2.Lines, l => l.Contains(PlanExecutor.HoldFolderName));
        confirm2.AcceptCommand.Execute(null);
        await step2;
        Assert.False(File.Exists(Path.Combine(_bed.Tree, "Downloads", "Rechnung_Strom.pdf")));
        Assert.True(Directory.Exists(Path.Combine(_bed.Tree, PlanExecutor.HoldFolderName)));
        Assert.Contains(vm.AllActions, r => r.Action.State == PlanActionState.Executed);

        // Undo puts everything back.
        var undo = vm.UndoCommand.ExecuteAsync(null);
        (await ConfirmationAsync(vm, undo)).AcceptCommand.Execute(null);
        await undo;
        Assert.Equal(tree, Snapshot(_bed.Tree));
        Assert.Equal(library, Snapshot(_bed.Library));
        Assert.Contains(vm.AllActions, r => r.Action.State == PlanActionState.Undone);
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
    public async Task KeepSource_IsTheSwitchOfTheJob()
    {
        await _bed.CreatePlanAsync();
        var vm = await _bed.OpenPlanAsync();
        Assert.False(vm.KeepSource);
        vm.KeepSource = true;
        var executor = _bed.GuiServices().GetRequiredService<PlanExecutor>();
        await WaitUntil(() => executor.GetKeepSourceAsync(_bed.JobId).GetAwaiter().GetResult());
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
