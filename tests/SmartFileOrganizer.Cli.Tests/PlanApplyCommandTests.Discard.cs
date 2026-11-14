using System.Text.Json;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// The kind <c>discard</c> (https://github.com/crack666/smart_file_organizer/issues/105): clutter goes into the hold folder
/// of its source without a copy anywhere, comes back with <c>sfo plan undo</c> and is removed for good only by
/// <c>sfo plan purge</c>. Same generated source and library as the other tests of this class.
/// </summary>
public partial class PlanApplyCommandTests
{
    [Fact]
    public async Task Discard_WritesNothingToALibrary_HoldsTheOriginal_AndUndoRestoresPlaceNameAndTime()
    {
        var source = Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf");
        // A modification time far from now, set before the scan, so that a rename that lost it would show.
        var time = new DateTime(2019, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, time);
        var (plan, invoice) = await DiscardPlanAsync("Downloads/Rechnung_Strom.pdf");
        var tree = Snapshot(_tree);
        var library = Snapshot(_library);
        var hash = Hash(source);
        var size = new FileInfo(source).Length;

        // Step 1 checks and writes nothing anywhere.
        var dry = await OkAsync("plan", "apply", plan);
        Assert.Equal("would_run", Str(Items(dry).Single(i => Id(i).ToString() == invoice), "outcome"));
        var step1 = await OkAsync("plan", "apply", plan, "--execute", "--action", invoice);
        var item = Items(step1).Single();
        Assert.Equal(("done", "verified", "discard"), (Str(item, "outcome"), Str(item, "state"), Str(item, "kind")));
        Assert.Equal(tree, Snapshot(_tree));
        Assert.Equal(library, Snapshot(_library));
        Assert.Equal(size, step1.GetProperty("removal").GetProperty("bytes").GetInt64());
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM plan_steps"));

        // The dry run of step 2 says what goes into the hold folder and what the purge would free; nothing moves.
        var dryRemove = await OkAsync("plan", "apply", plan, "--remove-sources", "--action", invoice);
        Assert.Equal("would_run", Str(Items(dryRemove).Single(), "outcome"));
        var wouldHold = dryRemove.GetProperty("held");
        Assert.Equal((1L, size, 1L), (wouldHold.GetProperty("files").GetInt64(), wouldHold.GetProperty("bytes").GetInt64(),
            wouldHold.GetProperty("discarded_files").GetInt64()));
        Assert.Equal(Path.Combine(_tree, PlanExecutor.HoldFolderName), Str(wouldHold, "folder"));
        Assert.True(File.Exists(source));

        var removed = await OkAsync("plan", "apply", plan, "--remove-sources", "--execute", "--action", invoice);
        Assert.Equal("executed", Str(Items(removed).Single(), "state"));
        Assert.False(File.Exists(source));
        var held = Assert.Single(Directory.GetFiles(Path.Combine(_tree, PlanExecutor.HoldFolderName), "*", SearchOption.AllDirectories));
        Assert.Equal(hash, Hash(held));
        Assert.Equal(size, removed.GetProperty("held").GetProperty("bytes").GetInt64());
        Assert.Equal(library, Snapshot(_library));
        // The journal says where it lies and that there is no target.
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM plan_steps WHERE kind = 'hold' AND state = 'done' AND target_path IS NULL"));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM moves WHERE target_path IS NULL AND returned_at IS NULL"));

        // Taken back: the same place, name, content and modification time.
        var undone = await OkAsync("plan", "undo", plan, "--execute");
        Assert.Equal("undone", Str(Items(undone).Single(), "state"));
        Assert.True(File.Exists(source));
        Assert.Equal(hash, Hash(source));
        Assert.Equal(time, File.GetLastWriteTimeUtc(source));
        Assert.Equal(tree, Snapshot(_tree));
        Assert.Equal(library, Snapshot(_library));
        Assert.Equal(0, undone.GetProperty("held").GetProperty("files").GetInt64());
    }

    [Fact]
    public async Task Discard_IsRemovedForGoodOnlyByPurge_AndThenCannotBeTakenBack()
    {
        var (plan, invoice) = await DiscardPlanAsync("Downloads/Rechnung_Strom.pdf");
        var source = Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf");
        var library = Snapshot(_library);
        await OkAsync("plan", "apply", plan, "--execute", "--action", invoice);
        await OkAsync("plan", "apply", plan, "--remove-sources", "--execute", "--action", invoice);
        var hold = Path.Combine(_tree, PlanExecutor.HoldFolderName);
        Assert.Single(Directory.GetFiles(hold, "*", SearchOption.AllDirectories));

        // Without --execute the purge only says what it would remove.
        var dry = await OkAsync("plan", "purge", plan);
        Assert.Equal("would_run", Str(Items(dry).Single(), "outcome"));
        Assert.Equal(0, dry.GetProperty("held").GetProperty("files").GetInt64());
        Assert.Single(Directory.GetFiles(hold, "*", SearchOption.AllDirectories));

        var purged = await OkAsync("plan", "purge", plan, "--execute");
        Assert.Equal("done", Str(Items(purged).Single(), "outcome"));
        Assert.False(Directory.Exists(hold));
        Assert.False(File.Exists(source));
        Assert.Equal(library, Snapshot(_library));
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM moves WHERE purged_at IS NOT NULL"));

        // No copy was ever made, so the undo says so and changes nothing.
        Assert.Equal("would_fail", Str(Items(await OkAsync("plan", "undo", plan)).Single(), "outcome"));
        var undo = await SfoAsync("plan", "undo", plan, "--execute");
        Assert.Equal(ExitCodes.Failure, undo.ExitCode);
        Assert.Equal(["not_undoable"], Strings(Items(undo.Result()).Single(), "reasons"));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task Discard_WithoutRelease_ChangesNothing()
    {
        var (job, _) = await PrepareAsync();
        var plan = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        var invoice = Id((await ActionsAsync(plan)).Single(a => Str(a, "relative_path") == "Downloads/Rechnung_Strom.pdf")).ToString();
        await OkAsync("plan", "approve", plan, "--action", invoice);
        // A change of kind takes the release back.
        var set = await OkAsync("plan", "set", plan, invoice, "--kind", "discard");
        Assert.Equal("proposed", Str(Items(set).Single(), "state"));
        var tree = Snapshot(_tree);

        var named = await OkAsync("plan", "apply", plan, "--execute", "--action", invoice);
        Assert.Equal(["not_approved", "proposed"], Strings(Items(named).Single(), "reasons"));
        await OkAsync("plan", "apply", plan, "--execute");
        await OkAsync("plan", "apply", plan, "--remove-sources", "--execute");
        await OkAsync("plan", "purge", plan, "--execute");
        Assert.Equal(tree, Snapshot(_tree));
        Assert.False(Directory.Exists(Path.Combine(_tree, PlanExecutor.HoldFolderName)));
    }

    [Fact]
    public async Task Discard_InAManagedArea_OrWithKeepSource_IsPassedOver()
    {
        var (plan, invoice) = await DiscardPlanAsync("Downloads/Rechnung_Strom.pdf");
        var tree = Snapshot(_tree);

        // After the release the folder becomes a managed area: the checks run again before the action.
        WriteSettings($$"""{ "Plan": { "ManagedAreas": [ {{JsonSerializer.Serialize(Path.Combine(_tree, "Downloads"))}} ] } }""");
        var run = await OkAsync("plan", "apply", plan, "--execute", "--action", invoice);
        Assert.Equal("skipped", Str(Items(run).Single(), "outcome"));
        Assert.Contains("source_in_managed_area", Strings(Items(run).Single(), "reasons"));
        var refused = await SfoAsync("plan", "set", plan, invoice, "--kind", "discard", "--reason", "again");
        Assert.Equal("plan_change_refused", refused.ErrorCode());
        WriteSettings("{}");

        // A job set to leave its sources alone removes nothing, discard included.
        var job = (await OkAsync("plan", "summary", plan)).GetProperty("job_id").GetInt64().ToString();
        await OkAsync("plan", "keep-source", job);
        var kept = await OkAsync("plan", "apply", plan, "--execute", "--action", invoice);
        Assert.Equal(["keep_source"], Strings(Items(kept).Single(), "reasons"));
        Assert.Equal(tree, Snapshot(_tree));
    }

    [Fact]
    public async Task ASensitiveDiscard_IsDecidedByAPerson_AndRunsOnlyWhenNamed()
    {
        var (job, _) = await PrepareAsync();
        var plan = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        var secret = Id((await ActionsAsync(plan)).Single(a => a.GetProperty("sensitive").GetBoolean())).ToString();

        var byAgent = await SfoAsync("plan", "set", plan, secret, "--kind", "discard", "--by", "agent");
        Assert.Equal("plan_change_refused", byAgent.ErrorCode());
        await OkAsync("plan", "set", plan, secret, "--kind", "discard");
        var all = await OkAsync("plan", "approve", plan, "--all");
        Assert.Contains(all.GetProperty("skipped").EnumerateArray(), s => Id(s).ToString() == secret);
        await OkAsync("plan", "approve", plan, "--action", secret);

        var batch = await OkAsync("plan", "apply", plan, "--execute");
        Assert.Equal(["single_decision"], Strings(Items(batch).Single(i => Id(i).ToString() == secret), "reasons"));
        await OkAsync("plan", "apply", plan, "--execute", "--action", secret);
        var removeAll = await OkAsync("plan", "apply", plan, "--remove-sources", "--execute");
        Assert.Equal(["single_decision"], Strings(Items(removeAll).Single(i => Id(i).ToString() == secret), "reasons"));
        Assert.True(File.Exists(Path.Combine(_tree, "Privat", "passwoerter.txt")));

        await OkAsync("plan", "apply", plan, "--remove-sources", "--execute", "--action", secret);
        Assert.False(File.Exists(Path.Combine(_tree, "Privat", "passwoerter.txt")));
    }

    [Fact]
    public async Task ADiscardCutOffBetweenTwoFiles_LosesNothing_AndTheNextRunFinishes()
    {
        var (plan, holiday) = await DiscardPlanAsync("Urlaub Kroatien 2021");
        var tree = Snapshot(_tree);
        var originals = tree.ToDictionary(kv => kv.Key, kv => kv.Value);
        var times = Directory.GetFiles(Path.Combine(_tree, "Urlaub Kroatien 2021")).ToDictionary(f => f, File.GetLastWriteTimeUtc);
        await OkAsync("plan", "apply", plan, "--execute", "--action", holiday);

        // Cut off with the first file held and the second checked and about to go.
        var holds = 0;
        _hook = (point, _) =>
        {
            if (point == PlanExecutionHooks.BeforeHold && ++holds == 2) throw new SimulatedCrash();
        };
        Assert.Equal("internal_error", (await SfoAsync("plan", "apply", plan, "--remove-sources", "--execute", "--action", holiday)).ErrorCode());
        _hook = null;
        AssertEveryOriginalIsSomewhere(originals);
        Assert.Single(Directory.GetFiles(Path.Combine(_tree, PlanExecutor.HoldFolderName), "*", SearchOption.AllDirectories));
        Assert.Equal("verified", Str((await ActionsAsync(plan)).Single(a => Id(a).ToString() == holiday), "state"));

        // The next run settles the open step and takes the rest; nothing is held twice.
        var finished = await OkAsync("plan", "apply", plan, "--remove-sources", "--execute", "--action", holiday);
        var item = Items(finished).Single();
        Assert.Equal(("done", "executed", 2L), (Str(item, "outcome"), Str(item, "state"), item.GetProperty("files").GetInt64()));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_tree, PlanExecutor.HoldFolderName), "*", SearchOption.AllDirectories).Length);
        Assert.Equal(3, finished.GetProperty("held").GetProperty("files").GetInt64());
        AssertEveryOriginalIsSomewhere(originals);

        await OkAsync("plan", "undo", plan, "--execute");
        Assert.Equal(tree, Snapshot(_tree));
        Assert.All(times, t => Assert.Equal(t.Value, File.GetLastWriteTimeUtc(t.Key)));
    }

    [Fact]
    public async Task TheMoveNotice_NamesADiscardedFileAsRemoved_AndWhereKnowsNoNewPlace()
    {
        var (plan, invoice) = await DiscardPlanAsync("Downloads/Rechnung_Strom.pdf");
        var source = Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf");
        var notice = Path.Combine(_tree, "Downloads", MoveNotice.FileName);
        await OkAsync("plan", "apply", plan, "--execute", "--action", invoice);
        await OkAsync("plan", "apply", plan, "--remove-sources", "--execute", "--action", invoice);

        var text = File.ReadAllText(notice);
        var line = Assert.Single(text.Split('\n'), l => l.Length > 0 && !l.StartsWith('#'));
        var fields = line.Split('\t');
        Assert.Equal("removed", fields[0]);
        Assert.Equal("Rechnung_Strom.pdf", fields[4]);
        Assert.Equal(string.Empty, fields[5]);
        var entry = Assert.Single(MoveNotice.Parse(text)!.Entries);
        Assert.True(entry.Removed);
        Assert.DoesNotContain(text.Split('\n'), l => l.StartsWith("moved\t", StringComparison.Ordinal));

        var where = await OkAsync("where", source);
        var step = Assert.Single(where.GetProperty("history").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, step.GetProperty("to").ValueKind);
        Assert.DoesNotContain(where.GetProperty("now").EnumerateArray(), p => Str(p, "path") == source);

        // A scan reads the notice as origin and stores the entry without a new place.
        await OkAsync("scan", _tree, "--refresh");
        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM notice_entries WHERE target_path = ''"));

        await OkAsync("plan", "undo", plan, "--execute");
        Assert.False(File.Exists(notice));
    }

    [Fact]
    public async Task Summary_SaysWhatGoesIntoTheHoldFolder_AndThatOnlyThePurgeFreesRoom()
    {
        var (plan, invoice) = await DiscardPlanAsync("Downloads/Rechnung_Strom.pdf");
        var size = new FileInfo(Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf")).Length;
        var before = await OkAsync("plan", "summary", plan);
        Assert.Equal(1, before.GetProperty("by_kind").GetProperty("discard").GetProperty("actions").GetInt64());
        var hold = before.GetProperty("hold");
        Assert.Equal(size, hold.GetProperty("discard_bytes").GetInt64());
        Assert.Equal(0, hold.GetProperty("held_bytes").GetInt64());
        Assert.Equal(Path.Combine(_tree, PlanExecutor.HoldFolderName), Str(hold, "folder"));

        await OkAsync("plan", "apply", plan, "--execute", "--action", invoice);
        await OkAsync("plan", "apply", plan, "--remove-sources", "--execute", "--action", invoice);
        var after = await OkAsync("plan", "summary", plan);
        Assert.Equal(size, after.GetProperty("hold").GetProperty("held_bytes").GetInt64());
        Assert.Equal(1, after.GetProperty("hold").GetProperty("held_files").GetInt64());

        var text = await SfoTextAsync("plan", "summary", plan);
        Assert.Contains("sfo plan purge", text);
        Assert.Contains("only", text);
    }

    /// <summary>A plan whose action at <paramref name="relative"/> is set to discard and released by a person.</summary>
    private async Task<(string Plan, string Action)> DiscardPlanAsync(string relative)
    {
        var (job, _) = await PrepareAsync();
        var plan = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        var action = Id((await ActionsAsync(plan)).Single(a => Str(a, "relative_path") == relative)).ToString();
        var set = await OkAsync("plan", "set", plan, action, "--kind", "discard");
        Assert.Equal("discard", Str(Items(set).Single(), "kind"));
        Assert.Empty(Strings(Items(set).Single(), "problems"));
        await OkAsync("plan", "approve", plan, "--action", action);
        return (plan, action);
    }

    private async Task<string> SfoTextAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--db", _dbPath], Host(stdout, stderr));
        Assert.True(exit == ExitCodes.Ok, stdout.ToString() + stderr);
        return stdout.ToString();
    }
}
