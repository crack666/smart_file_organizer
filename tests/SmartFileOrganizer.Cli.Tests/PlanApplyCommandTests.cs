using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartFileOrganizer.Application.Evaluation;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Cli.Contract;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Tests.Support;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// <c>sfo plan apply|undo|purge</c> (https://github.com/crack666/smart_file_organizer/issues/12), in-process on a
/// generated source and a generated library, both in a temp folder this test makes. No model runs; every name and every
/// file is invented. Runs are cut off through <see cref="PlanExecutionHooks"/>, as a crash or Ctrl+C would.
/// </summary>
public class PlanApplyCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _corpus;
    private readonly string _tree;
    private readonly string _library;
    private readonly string _profile;
    private readonly List<TruthDirectoryEntry> _dirs = [new(".", "mixed")];
    private readonly List<TruthFileEntry> _files = [];
    private Action<string, string>? _hook;

    public PlanApplyCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_apply_{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_dir, "state", "data.db");
        _corpus = Path.Combine(_dir, "corpus");
        _tree = Path.Combine(_corpus, "tree");
        _library = Path.Combine(_dir, "library");
        _profile = Path.Combine(_dir, "profile");
        Directory.CreateDirectory(_profile);

        // A uniform folder: one action for the folder.
        Folder("Urlaub Kroatien 2021", "uniform");
        for (var i = 0; i < 3; i++)
            Write($"Urlaub Kroatien 2021/IMG_20210703_1{i}0000.jpg", "photo", $"croatia {i} " + new string('x', 1000 * (i + 1)));

        // A mixed folder: one action per worth-keeping file.
        Folder("Downloads", "mixed");
        Write("Downloads/IMG_20230812_143012.jpg", "photo", "download photo 1");
        Write("Downloads/IMG_20230812_143512.jpg", "photo", "download photo 2");
        Write("Downloads/Rechnung_Strom.pdf", "invoice", "%PDF-1.4 power");

        // Sensitive: decided one by one.
        Folder("Privat", "mixed");
        Write("Privat/passwoerter.txt", "document", "mail: dummy-not-real", ["credentials"]);

        var truth = new EvalTruthDocument(EvalTruth.SchemaVersion, new EvalCorpusInfo("apply-test", 1, 7), "tree", _dirs, _files);
        File.WriteAllText(Path.Combine(_corpus, EvalTruth.FileName), JsonSerializer.Serialize(truth, EvalJson.Options));

        Directory.CreateDirectory(Path.Combine(_library, "Fotos", "2019-06 Italien"));
        File.WriteAllText(Path.Combine(_library, "Fotos", "2019-06 Italien", "IMG_20190612_100000.jpg"), "italy");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { FileSystemLinks.DeleteTree(_dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    // ── Fertig, wenn ─────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyExecute_RemoveSources_ThenUndo_LeaveTheSameTreesWithTheSameChecksums()
    {
        var plan = await ApprovedPlanAsync();
        var tree = Snapshot(_tree);
        var library = Snapshot(_library);

        var copied = await OkAsync("plan", "apply", plan, "--execute");
        Assert.All(Items(copied), i => Assert.Equal("done", Str(i, "outcome")));
        Assert.All(Items(copied), i => Assert.Equal("verified", Str(i, "state")));
        Assert.Equal(6, copied.GetProperty("removal").GetProperty("files").GetInt64());
        // Step 1 leaves every original in place; the library holds a checked copy of each.
        Assert.Equal(tree, Snapshot(_tree));
        Assert.Equal(Hash(Path.Combine(_tree, "Urlaub Kroatien 2021", "IMG_20210703_120000.jpg")),
            Hash(Path.Combine(_library, "Fotos", "Urlaub Kroatien 2021", "IMG_20210703_120000.jpg")));
        Assert.Equal(Hash(Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf")), Hash(Path.Combine(_library, "Dokumente", "Rechnung_Strom.pdf")));
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf")),
            File.GetLastWriteTimeUtc(Path.Combine(_library, "Dokumente", "Rechnung_Strom.pdf")));

        var removed = await OkAsync("plan", "apply", plan, "--remove-sources", "--execute");
        Assert.All(Items(removed), i => Assert.Equal("executed", Str(i, "state")));
        Assert.False(File.Exists(Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf")));
        Assert.True(File.Exists(Path.Combine(_tree, "Privat", "passwoerter.txt")));
        // Emptied source folders stay and are reported.
        Assert.True(Directory.Exists(Path.Combine(_tree, "Urlaub Kroatien 2021")));
        Assert.Contains(Path.Combine(_tree, "Urlaub Kroatien 2021"), Strings(removed, "empty_folders"));
        // The originals are held on their own drive, nothing is gone for good.
        Assert.Equal(6, Directory.GetFiles(Path.Combine(_tree, PlanExecutor.HoldFolderName), "*", SearchOption.AllDirectories).Length);

        var undone = await OkAsync("plan", "undo", plan, "--execute");
        Assert.All(Items(undone), i => Assert.Equal("undone", Str(i, "state")));
        Assert.Equal(tree, Snapshot(_tree));
        Assert.Equal(library, Snapshot(_library));
    }

    [Fact]
    public async Task ACutOffRun_AfterCopyAndBeforeRemoval_LosesNothing_AndTheNextRunFinishes()
    {
        var plan = await ApprovedPlanAsync();
        var tree = Snapshot(_tree);
        var library = Snapshot(_library);
        var originals = tree.ToDictionary(kv => kv.Key, kv => kv.Value);

        // Cut off in step 1, after the second copy carries its name and before the journal says so.
        var copies = 0;
        _hook = (point, _) =>
        {
            if (point == PlanExecutionHooks.Copied && ++copies == 2) throw new SimulatedCrash();
        };
        Assert.Equal("internal_error", (await SfoAsync("plan", "apply", plan, "--execute")).ErrorCode());
        _hook = null;
        Assert.Equal(tree, Snapshot(_tree));

        var finished = await OkAsync("plan", "apply", plan, "--execute");
        Assert.All(Items(finished), i => Assert.Equal("verified", Str(i, "state")));
        Assert.Empty(Directory.GetFiles(_library, "*" + PlanExecutor.PartialSuffix, SearchOption.AllDirectories));

        // Cut off in step 2, with the second original checked and about to go.
        var holds = 0;
        _hook = (point, _) =>
        {
            if (point == PlanExecutionHooks.BeforeHold && ++holds == 2) throw new SimulatedCrash();
        };
        Assert.Equal("internal_error", (await SfoAsync("plan", "apply", plan, "--remove-sources", "--execute")).ErrorCode());
        _hook = null;
        AssertEveryOriginalIsSomewhere(originals);

        var removed = await OkAsync("plan", "apply", plan, "--remove-sources", "--execute");
        Assert.All(Items(removed), i => Assert.Equal("executed", Str(i, "state")));
        AssertEveryOriginalIsSomewhere(originals);
        Assert.False(File.Exists(Path.Combine(_tree, "Downloads", "IMG_20230812_143012.jpg")));

        await OkAsync("plan", "undo", plan, "--execute");
        Assert.Equal(tree, Snapshot(_tree));
        Assert.Equal(library, Snapshot(_library));
    }

    // ── Umzugsnotiz und sfo where (Issue 86) ─────────────────────────────

    [Fact]
    public async Task RemoveSources_LeavesAMoveNotice_WhereNamesTheNewPlace_AndUndoRemovesTheNotice()
    {
        var plan = await ApprovedPlanAsync();
        var tree = Snapshot(_tree);
        var source = Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf");
        var target = Path.Combine(_library, "Dokumente", "Rechnung_Strom.pdf");
        var notice = Path.Combine(_tree, "Downloads", MoveNotice.FileName);
        await OkAsync("plan", "apply", plan, "--execute");
        // The dry run of step 2 names the notice it would write and writes none.
        var dryRemove = await OkAsync("plan", "apply", plan, "--remove-sources");
        Assert.Contains(dryRemove.GetProperty("notices").EnumerateArray(),
            n => Str(n, "path") == notice && Str(n, "outcome") == "would_write" && n.GetProperty("entries").GetInt32() == 3);
        Assert.False(File.Exists(notice));
        var removed = await OkAsync("plan", "apply", plan, "--remove-sources", "--execute");

        // The notice lies in the source folder, readable without sfo: names and new places, nothing about the content.
        Assert.True(File.Exists(notice));
        var text = File.ReadAllText(notice);
        Assert.StartsWith("# sfo-move-notice 1\n", text);
        var doc = MoveNotice.Parse(text);
        Assert.NotNull(doc);
        Assert.Equal(3, doc.Entries.Count);
        var entry = doc.Entries.Single(e => e.Name == "Rechnung_Strom.pdf");
        Assert.Equal(target, entry.Target);
        Assert.Equal(long.Parse(plan), entry.PlanId);
        Assert.DoesNotContain("power", text);
        Assert.Contains(removed.GetProperty("notices").EnumerateArray(), n => Str(n, "path") == notice && Str(n, "outcome") == "written");
        // The emptied folder stays, holds only its notice and is still reported as left empty.
        var holiday = Path.Combine(_tree, "Urlaub Kroatien 2021");
        Assert.Equal([MoveNotice.FileName], Directory.GetFileSystemEntries(holiday).Select(Path.GetFileName));
        Assert.Contains(holiday, Strings(removed, "empty_folders"));

        // sfo where: by the old path and by the checksum.
        var where = await OkAsync("where", source);
        Assert.Equal(target, Str(where.GetProperty("now").EnumerateArray().First(), "path"));
        var step = Assert.Single(where.GetProperty("history").EnumerateArray());
        Assert.Equal("moved", Str(step, "state"));
        Assert.Equal(long.Parse(plan), step.GetProperty("plan_id").GetInt64());
        Assert.Equal(target, Str(step, "to"));
        var byHash = await OkAsync("where", Hash(target));
        Assert.Contains(byHash.GetProperty("now").EnumerateArray(), p => Str(p, "path") == target);
        Assert.DoesNotContain(byHash.GetProperty("now").EnumerateArray(), p => Str(p, "path") == source);

        // The dry run of the undo says the notice would go, and it stays.
        var dryUndo = await OkAsync("plan", "undo", plan);
        Assert.Contains(dryUndo.GetProperty("notices").EnumerateArray(), n => Str(n, "path") == notice && Str(n, "outcome") == "would_remove");
        Assert.DoesNotContain(dryUndo.GetProperty("notices").EnumerateArray(), n => Str(n, "outcome") == "removed");
        Assert.True(File.Exists(notice));

        // Undo takes the notices back with the originals: the source tree is as before.
        var undone = await OkAsync("plan", "undo", plan, "--execute");
        Assert.False(File.Exists(notice));
        Assert.Contains(undone.GetProperty("notices").EnumerateArray(), n => Str(n, "path") == notice && Str(n, "outcome") == "removed");
        Assert.Equal(tree, Snapshot(_tree));
        var back = await OkAsync("where", source);
        Assert.Equal("returned", Str(Assert.Single(back.GetProperty("history").EnumerateArray()), "state"));
        Assert.Equal(source, Str(back.GetProperty("now").EnumerateArray().First(), "path"));
    }

    [Fact]
    public async Task AFileWithTheNoticeName_IsNeverTouched_AndNoNoticeWritesNone()
    {
        var plan = await ApprovedPlanAsync();
        var foreign = Path.Combine(_tree, "Downloads", MoveNotice.FileName);
        await OkAsync("plan", "apply", plan, "--execute");
        // Written after the scan, so the plan does not know it; a person's own file that carries the name.
        File.WriteAllText(foreign, "my own list");

        var actions = await ActionsAsync(plan);
        var holiday = Id(actions.Single(a => Str(a, "source_kind") == "folder")).ToString();
        var invoice = Id(actions.Single(a => Str(a, "relative_path") == "Downloads/Rechnung_Strom.pdf")).ToString();
        var run = await OkAsync("plan", "apply", plan, "--remove-sources", "--execute", "--action", invoice);
        Assert.Equal("my own list", File.ReadAllText(foreign));
        Assert.Contains(run.GetProperty("notices").EnumerateArray(), n => Str(n, "path") == foreign && Str(n, "outcome") == "skipped");

        await OkAsync("plan", "apply", plan, "--remove-sources", "--execute", "--no-notice", "--action", holiday);
        Assert.False(File.Exists(Path.Combine(_tree, "Urlaub Kroatien 2021", MoveNotice.FileName)));
        await OkAsync("plan", "undo", plan, "--execute");
        Assert.Equal("my own list", File.ReadAllText(foreign));
    }

    [Fact]
    public async Task TheScanSkipsNoticeAndHoldFolder_ReadsTheNoticeAsOrigin_AndARefreshNamesTheTargets()
    {
        var plan = await ApprovedPlanAsync();
        await OkAsync("plan", "apply", plan, "--execute");
        await OkAsync("plan", "apply", plan, "--remove-sources", "--execute");
        Assert.True(File.Exists(Path.Combine(_tree, "Downloads", MoveNotice.FileName)));

        var scan = await OkAsync("scan", _tree, "--refresh");
        var job = scan.GetProperty("job").GetProperty("id").GetInt64();
        // Left in the tree: the sensitive file only. Neither the notices nor the held originals count as content.
        Assert.Equal(1, scan.GetProperty("job").GetProperty("files_total").GetInt64());
        var refresh = scan.GetProperty("refresh");
        Assert.Equal(6, refresh.GetProperty("moved").GetInt64());
        Assert.Equal(0, refresh.GetProperty("new").GetInt64());
        Assert.Equal(0, refresh.GetProperty("missing").GetInt64());
        var moved = refresh.GetProperty("changes").EnumerateArray().Single(c => Str(c, "path") == Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf"));
        Assert.Equal("moved", Str(moved, "change"));
        Assert.Equal(Path.Combine(_library, "Dokumente", "Rechnung_Strom.pdf"), Str(moved, "target"));

        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT (SELECT COUNT(*) FROM file_nodes WHERE job_id = {job} AND name = '{MoveNotice.FileName}'), " +
                          $"(SELECT COUNT(*) FROM notice_entries WHERE job_id = {job})";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt64(0));
        Assert.Equal(6, reader.GetInt64(1));
    }

    // ── Bestand der Library fortschreiben (Issue 86) ─────────────────────

    [Fact]
    public async Task AfterApply_TheLibraryHoldsTheCopies_WithoutANewScan_AndUndoTakesThemBack()
    {
        var (job, library) = await PrepareAsync();
        var plan = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        await OkAsync("plan", "approve", plan, "--all");
        var before = BackedUp(await OkAsync("findings", job, "--summary"));
        var libraryFiles = await ScalarAsync($"SELECT COUNT(*) FROM file_nodes WHERE job_id = {library}");
        var copy = Path.Combine(_library, "Dokumente", "Rechnung_Strom.pdf");

        await OkAsync("plan", "apply", plan, "--execute");
        // Step 2 runs although the source now counts as backed up: that is the plan's own copy.
        var removed = await OkAsync("plan", "apply", plan, "--remove-sources", "--execute");
        Assert.All(Items(removed), i => Assert.Equal("executed", Str(i, "state")));

        // The library was not scanned again, and still holds the six copies, each with its checksum.
        Assert.Equal(libraryFiles + 6, await ScalarAsync($"SELECT COUNT(*) FROM file_nodes WHERE job_id = {library}"));
        Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM scan_jobs WHERE role = 'library'"));
        Assert.Equal(1, await ScalarAsync(
            $"SELECT COUNT(*) FROM file_nodes n JOIN content_fingerprints f ON f.full_path = n.full_path " +
            $"WHERE n.job_id = {library} AND n.full_path = '{copy}' AND f.full_hash = '{Hash(copy)}'"));
        var after = BackedUp(await OkAsync("findings", job, "--summary"));
        Assert.Equal(before + 6, after);

        // The undo takes the copies out of the library again, and the source is as unsafe as before.
        await OkAsync("plan", "undo", plan, "--execute");
        Assert.Equal(libraryFiles, await ScalarAsync($"SELECT COUNT(*) FROM file_nodes WHERE job_id = {library}"));
        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM content_fingerprints WHERE full_path = '{copy}'"));
        Assert.Equal(before, BackedUp(await OkAsync("findings", job, "--summary")));
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM library_entries WHERE undone_at IS NULL"));
    }

    [Fact]
    public async Task ALaterScanOfTheLibrary_ListsEachCopyOnce()
    {
        var (job, library) = await PrepareAsync();
        var plan = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        await OkAsync("plan", "approve", plan, "--all");
        await OkAsync("plan", "apply", plan, "--execute");
        var held = await ScalarAsync($"SELECT COUNT(*) FROM file_nodes WHERE job_id = {library}");

        // The written-forward rows are the files a scan finds: a refresh sees nothing new, nothing changed.
        var scan = await OkAsync("scan", _library, "--role", "library", "--refresh");
        var again = scan.GetProperty("job").GetProperty("id").GetInt64();
        var refresh = scan.GetProperty("refresh");
        Assert.Equal(0, refresh.GetProperty("new").GetInt64());
        Assert.Equal(0, refresh.GetProperty("changed").GetInt64());
        Assert.Equal(0, refresh.GetProperty("missing").GetInt64());
        Assert.Equal(held, await ScalarAsync($"SELECT COUNT(*) FROM file_nodes WHERE job_id = {again}"));
        Assert.Equal(held, await ScalarAsync($"SELECT COUNT(DISTINCT full_path) FROM file_nodes WHERE job_id = {again}"));

        // Compared with the new library job, the source is backed up once per file, not twice.
        await OkAsync("compare", job, "--against", again.ToString());
        var summary = await OkAsync("findings", job, "--summary");
        var byLibrary = summary.GetProperty("overview").GetProperty("backed_up").GetProperty("by_library").EnumerateArray().ToList();
        Assert.Equal(6, byLibrary.Single(l => l.GetProperty("library_job_id").GetInt64() == again).GetProperty("files").GetInt64());
    }

    private static long BackedUp(JsonElement summary) =>
        summary.GetProperty("overview").GetProperty("backed_up").GetProperty("files").GetInt64();

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // ── Löschregel (Nachtrag 12.11.2026) ─────────────────────────────────

    [Fact]
    public async Task AnOriginalStays_WhenTheCopyChangedAfterItWasChecked()
    {
        var plan = await ApprovedPlanAsync();
        await OkAsync("plan", "apply", plan, "--execute");
        var copy = Path.Combine(_library, "Dokumente", "Rechnung_Strom.pdf");
        File.WriteAllText(copy, "%PDF-1.4 edited in the library");

        var run = await SfoAsync("plan", "apply", plan, "--remove-sources", "--execute");
        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal("plan_run_incomplete", run.ErrorCode());
        var invoice = Items(run.Result()).Single(i => Str(i, "outcome") == "failed");
        Assert.Equal(["target_changed"], Strings(invoice, "reasons"));
        Assert.True(File.Exists(Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf")));
        Assert.Equal("%PDF-1.4 power", File.ReadAllText(Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("differs")]
    public async Task DeleteSource_IsRefused_WhenTheLibraryFileIsGoneOrDiffers(string how)
    {
        var (plan, invoice, libraryCopy) = await DeleteSourcePlanAsync(link: false);
        var verified = await OkAsync("plan", "apply", plan, "--execute", "--action", invoice);
        Assert.Equal("verified", Str(Items(verified).Single(), "state"));

        if (how == "missing") File.Delete(libraryCopy);
        else File.WriteAllText(libraryCopy, "%PDF-1.4 other");

        var run = await SfoAsync("plan", "apply", plan, "--remove-sources", "--execute", "--action", invoice);
        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        var item = Items(run.Result()).Single();
        Assert.Equal("failed", Str(item, "state"));
        Assert.Equal([how == "missing" ? "target_missing" : "target_changed"], Strings(item, "reasons"));
        Assert.Equal("%PDF-1.4 power", File.ReadAllText(Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf")));
    }

    [Fact]
    public async Task DeleteSource_WithTheSameContentInTheLibrary_HoldsTheOriginal_AndUndoBringsItBack()
    {
        var (plan, invoice, libraryCopy) = await DeleteSourcePlanAsync(link: false);
        var tree = Snapshot(_tree);
        await OkAsync("plan", "apply", plan, "--execute", "--action", invoice);
        await OkAsync("plan", "apply", plan, "--remove-sources", "--execute", "--action", invoice);
        Assert.False(File.Exists(Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf")));
        Assert.True(File.Exists(libraryCopy));

        // Purged: the undo copies it back from the library, whose file stays.
        await OkAsync("plan", "purge", plan, "--execute");
        await OkAsync("plan", "undo", plan, "--execute");
        Assert.Equal(tree, Snapshot(_tree));
        Assert.True(File.Exists(libraryCopy));
    }

    [LinkFact(LinkNeed.HardLink)]
    public async Task SourceAndTarget_AsOneFile_AreRecognised_AndNothingIsRemoved()
    {
        var (plan, invoice, _) = await DeleteSourcePlanAsync(link: true);
        var run = await SfoAsync("plan", "apply", plan, "--execute", "--action", invoice);
        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        var item = Items(run.Result()).Single();
        Assert.Equal(["same_file"], Strings(item, "reasons"));
        Assert.Equal("failed", Str(item, "state"));
        Assert.True(File.Exists(Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf")));
    }

    // ── Sicherheitsanforderungen ─────────────────────────────────────────

    [Fact]
    public async Task WithoutExecute_ItIsADryRun_AndOnlyApprovedActionsRun()
    {
        var (job, _) = await PrepareAsync();
        var plan = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        var actions = await ActionsAsync(plan);
        var invoice = Id(actions.Single(a => Str(a, "relative_path") == "Downloads/Rechnung_Strom.pdf"));
        await OkAsync("plan", "approve", plan, "--action", invoice.ToString());
        var tree = Snapshot(_tree);
        var library = Snapshot(_library);

        var dry = await OkAsync("plan", "apply", plan);
        Assert.False(dry.GetProperty("executed").GetBoolean());
        Assert.Equal([invoice], Items(dry).Select(Id));
        Assert.Equal("would_run", Str(Items(dry).Single(), "outcome"));
        Assert.Equal(tree, Snapshot(_tree));
        Assert.Equal(library, Snapshot(_library));

        var done = await OkAsync("plan", "apply", plan, "--execute");
        Assert.Equal([invoice], Items(done).Select(Id));
        // The proposed folder action was not touched.
        Assert.False(Directory.Exists(Path.Combine(_library, "Fotos", "Urlaub Kroatien 2021")));
        Assert.Equal("proposed", Str((await ActionsAsync(plan)).Single(a => Str(a, "source_kind") == "folder"), "state"));

        var named = await OkAsync("plan", "apply", plan, "--action", Id(actions.Single(a => Str(a, "source_kind") == "folder")).ToString());
        Assert.Equal(["not_approved", "proposed"], Strings(Items(named).Single(), "reasons"));
    }

    [Fact]
    public async Task ATargetThatAppeared_IsNeverOverwritten_SameContentCountsAsPresent()
    {
        var plan = await ApprovedPlanAsync();
        var actions = await ActionsAsync(plan);
        var invoice = Id(actions.Single(a => Str(a, "relative_path") == "Downloads/Rechnung_Strom.pdf")).ToString();
        var photo = Id(actions.Single(a => Str(a, "relative_path") == "Downloads/IMG_20230812_143012.jpg")).ToString();
        var invoiceTarget = Path.Combine(_library, "Dokumente", "Rechnung_Strom.pdf");
        var photoTarget = Path.Combine(_library, "Fotos", "IMG_20230812_143012.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(invoiceTarget)!);
        File.WriteAllText(invoiceTarget, "someone else's invoice");
        File.Copy(Path.Combine(_tree, "Downloads", "IMG_20230812_143012.jpg"), photoTarget);

        var run = await SfoAsync("plan", "apply", plan, "--execute", "--action", invoice, "--action", photo);
        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        var items = Items(run.Result()).ToDictionary(i => Id(i).ToString());
        Assert.Equal(["target_collision"], Strings(items[invoice], "reasons"));
        Assert.Equal("failed", Str(items[invoice], "state"));
        Assert.Equal("someone else's invoice", File.ReadAllText(invoiceTarget));
        Assert.Equal(["already_present"], Strings(items[photo], "reasons"));
        Assert.Equal("verified", Str(items[photo], "state"));

        // The original goes only after both are read again; the undo leaves the file it did not write.
        await OkAsync("plan", "apply", plan, "--remove-sources", "--execute", "--action", photo);
        Assert.False(File.Exists(Path.Combine(_tree, "Downloads", "IMG_20230812_143012.jpg")));
        await OkAsync("plan", "undo", plan, "--execute", "--action", photo);
        Assert.True(File.Exists(Path.Combine(_tree, "Downloads", "IMG_20230812_143012.jpg")));
        Assert.True(File.Exists(photoTarget));
    }

    [Fact]
    public async Task ASourceChangedSinceTheScan_IsSkipped()
    {
        var plan = await ApprovedPlanAsync();
        var photo = Path.Combine(_tree, "Downloads", "IMG_20230812_143512.jpg");
        File.AppendAllText(photo, " edited");

        var run = await OkAsync("plan", "apply", plan, "--execute");
        var item = Items(run).Single(i => Strings(i, "reasons").Contains("source_changed"));
        Assert.Equal("skipped", Str(item, "outcome"));
        Assert.Equal("approved", Str(item, "state"));
        Assert.False(File.Exists(Path.Combine(_library, "Fotos", "IMG_20230812_143512.jpg")));
    }

    [Fact]
    public async Task TheChecksOfThePlanRunAgain_BeforeEveryAction()
    {
        var plan = await ApprovedPlanAsync();
        // After the confirmation the library becomes a protected place: no action may write there now.
        WriteSettings($$"""{ "Plan": { "ProtectedPlaces": [ {{JsonSerializer.Serialize(_library)}} ] } }""");
        var library = Snapshot(_library);

        var run = await OkAsync("plan", "apply", plan, "--execute");
        Assert.NotEmpty(Items(run));
        Assert.All(Items(run), i =>
        {
            Assert.Equal("skipped", Str(i, "outcome"));
            Assert.Contains("target_protected", Strings(i, "reasons"));
        });
        Assert.Equal(library, Snapshot(_library));
    }

    [Fact]
    public async Task ACopyWithAnotherChecksum_IsRemoved_AndTheOriginalStays()
    {
        var plan = await ApprovedPlanAsync();
        var invoice = Id((await ActionsAsync(plan)).Single(a => Str(a, "relative_path") == "Downloads/Rechnung_Strom.pdf")).ToString();
        _hook = (point, path) =>
        {
            if (point == PlanExecutionHooks.PartialWritten) File.AppendAllText(path, "bit rot");
        };

        var run = await SfoAsync("plan", "apply", plan, "--execute", "--action", invoice);
        Assert.Equal(ExitCodes.Failure, run.ExitCode);
        Assert.Equal(["hash_mismatch"], Strings(Items(run.Result()).Single(), "reasons"));
        Assert.False(Directory.Exists(Path.Combine(_library, "Dokumente")));
        Assert.True(File.Exists(Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf")));
    }

    [Fact]
    public async Task ASensitiveAction_RunsOnlyWhenNamed()
    {
        var plan = await ApprovedPlanAsync();
        var secret = Id((await ActionsAsync(plan)).Single(a => a.GetProperty("sensitive").GetBoolean())).ToString();
        await OkAsync("plan", "set", plan, secret, "--kind", "move", "--target", "Privat/passwoerter.txt");
        await OkAsync("plan", "approve", plan, "--action", secret);

        var batch = await OkAsync("plan", "apply", plan, "--execute");
        Assert.Equal(["single_decision"], Strings(Items(batch).Single(i => Id(i).ToString() == secret), "reasons"));
        Assert.False(File.Exists(Path.Combine(_library, "Privat", "passwoerter.txt")));

        var named = await OkAsync("plan", "apply", plan, "--execute", "--action", secret);
        Assert.Equal("verified", Str(Items(named).Single(), "state"));
        var removeAll = await OkAsync("plan", "apply", plan, "--remove-sources", "--execute");
        Assert.Equal(["single_decision"], Strings(Items(removeAll).Single(i => Id(i).ToString() == secret), "reasons"));
        Assert.True(File.Exists(Path.Combine(_tree, "Privat", "passwoerter.txt")));
    }

    [Fact]
    public async Task KeepSource_TurnsAMoveIntoACopy_AndRemovesNothing()
    {
        var (job, _) = await PrepareAsync();
        var plan = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        await OkAsync("plan", "approve", plan, "--all");
        var keep = await OkAsync("plan", "keep-source", job);
        Assert.True(keep.GetProperty("keep_source").GetBoolean());
        var tree = Snapshot(_tree);

        var run = await OkAsync("plan", "apply", plan, "--execute");
        Assert.All(Items(run), i =>
        {
            Assert.Equal("executed", Str(i, "state"));
            Assert.Contains("keep_source", Strings(i, "reasons"));
        });
        Assert.True(File.Exists(Path.Combine(_library, "Dokumente", "Rechnung_Strom.pdf")));
        var removed = await OkAsync("plan", "apply", plan, "--remove-sources", "--execute");
        Assert.Empty(Items(removed));
        Assert.Equal(tree, Snapshot(_tree));

        Assert.False((await OkAsync("plan", "keep-source", job, "--off")).GetProperty("keep_source").GetBoolean());
    }

    [Fact]
    public async Task Purge_RemovesHeldOriginalsOnlyWithACheckedCopy_AndUndoCopiesBackFromTheLibrary()
    {
        var plan = await ApprovedPlanAsync();
        var tree = Snapshot(_tree);
        var library = Snapshot(_library);
        await OkAsync("plan", "apply", plan, "--execute");
        await OkAsync("plan", "apply", plan, "--remove-sources", "--execute");

        // One copy changed in the library: its held original is the last one and stays.
        var changed = Path.Combine(_library, "Fotos", "IMG_20230812_143512.jpg");
        var before = File.ReadAllBytes(changed);
        File.WriteAllText(changed, "edited");
        var dry = await OkAsync("plan", "purge", plan);
        Assert.All(Items(dry), i => Assert.Equal("would_run", Str(i, "outcome")));
        var purge = await SfoAsync("plan", "purge", plan, "--execute");
        Assert.Equal(ExitCodes.Failure, purge.ExitCode);
        Assert.Equal(["target_changed"], Strings(Items(purge.Result()).Single(i => Str(i, "outcome") == "partial"), "reasons"));
        var held = Directory.GetFiles(Path.Combine(_tree, PlanExecutor.HoldFolderName), "*", SearchOption.AllDirectories);
        Assert.Equal("IMG_20230812_143512.jpg", Path.GetFileName(Assert.Single(held)));

        File.WriteAllBytes(changed, before);
        File.SetLastWriteTimeUtc(changed, File.GetLastWriteTimeUtc(Assert.Single(held)));
        await OkAsync("plan", "undo", plan, "--execute");
        Assert.Equal(tree, Snapshot(_tree));
        Assert.Equal(library, Snapshot(_library));
    }

    [LinkFact(LinkNeed.DirectoryLink)]
    public async Task ALinkInTheSource_IsNotFollowed()
    {
        var plan = await ApprovedPlanAsync();
        var elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "outside.txt"), "outside");
        FileSystemLinks.DirectoryLink(Path.Combine(_tree, "Urlaub Kroatien 2021", "link"), elsewhere);

        var run = await OkAsync("plan", "apply", plan, "--execute");
        var folder = Items(run).Single(i => Strings(i, "reasons").Contains("source_has_link"));
        Assert.Equal("skipped", Str(folder, "outcome"));
        Assert.False(Directory.Exists(Path.Combine(_library, "Fotos", "Urlaub Kroatien 2021")));
        Assert.True(File.Exists(Path.Combine(elsewhere, "outside.txt")));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private sealed class SimulatedCrash : Exception;

    private sealed class FixedPlaces(params string[] places) : IProtectedPlaces
    {
        public IReadOnlyList<string> Get() => places;
    }

    /// <summary>Every original is in its place or in the hold folder, with its content, and nothing is lost.</summary>
    private void AssertEveryOriginalIsSomewhere(IReadOnlyDictionary<string, string> originals)
    {
        var hold = Path.Combine(_tree, PlanExecutor.HoldFolderName);
        var held = Directory.Exists(hold)
            ? Directory.GetFiles(hold, "*", SearchOption.AllDirectories).Select(Hash).ToHashSet()
            : [];
        foreach (var (rel, hash) in originals.Where(o => o.Value != "folder"))
        {
            var path = Path.Combine(_tree, rel);
            Assert.True(File.Exists(path) ? Hash(path) == hash : held.Contains(hash), $"{rel} is lost");
        }
    }

    /// <summary>A plan whose invoice action is turned into delete_source against a library file with the same content (or a hard link to it).</summary>
    private async Task<(string Plan, string Invoice, string LibraryCopy)> DeleteSourcePlanAsync(bool link)
    {
        var (job, _) = await PrepareAsync();
        var plan = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        var invoice = Id((await ActionsAsync(plan)).Single(a => Str(a, "relative_path") == "Downloads/Rechnung_Strom.pdf")).ToString();
        var copy = Path.Combine(_library, "Archiv", "Rechnung_Strom.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        if (link) FileSystemLinks.HardLink(copy, Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf"));
        else File.Copy(Path.Combine(_tree, "Downloads", "Rechnung_Strom.pdf"), copy);
        await OkAsync("plan", "set", plan, invoice, "--kind", "delete_source", "--target", copy);
        await OkAsync("plan", "approve", plan, "--action", invoice);
        return (plan, invoice, copy);
    }

    private async Task<string> ApprovedPlanAsync()
    {
        var (job, _) = await PrepareAsync();
        var plan = (await OkAsync("plan", "create", job)).GetProperty("plan_id").GetInt64().ToString();
        await OkAsync("plan", "approve", plan, "--all");
        var approved = (await ActionsAsync(plan)).Where(a => Str(a, "state") == "approved").ToList();
        Assert.Equal(4, approved.Count);
        return plan;
    }

    /// <summary>Relative path of every file and folder below <paramref name="root"/>, with the checksum of each file.</summary>
    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            result[Path.GetRelativePath(root, dir) + "/"] = "folder";
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            result[Path.GetRelativePath(root, file)] = Hash(file);
        return result;
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private void Folder(string path, string verdict) => _dirs.Add(new TruthDirectoryEntry(path, verdict));

    private void Write(string relative, string category, string content, IReadOnlyList<string>? sensitive = null)
    {
        var path = Path.Combine(_tree, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _files.Add(new TruthFileEntry(relative, category, sensitive ?? []));
    }

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dbPath)!, "settings.json"), json);
    }

    private sealed record Run(int ExitCode, string Stdout, string Stderr)
    {
        public JsonElement Result()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.GetProperty("result").Clone();
        }

        public string? ErrorCode()
        {
            using var doc = JsonDocument.Parse(Stdout);
            return doc.RootElement.GetProperty("error").GetProperty("code").GetString();
        }
    }

    private CliHost Host(StringWriter stdout, StringWriter stderr) => new()
    {
        Out = stdout,
        Error = stderr,
        ConfigureServices = s =>
        {
            s.AddSingleton<IProtectedPlaces>(new FixedPlaces(_profile));
            s.AddSingleton(new PlanExecutionHooks { At = (point, path) => _hook?.Invoke(point, path) });
        }
    };

    private async Task<Run> SfoAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await SfoApp.RunAsync([.. args, "--json", "--db", _dbPath], Host(stdout, stderr));
        return new Run(exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<JsonElement> OkAsync(params string[] args)
    {
        var run = await SfoAsync(args);
        Assert.True(run.ExitCode == ExitCodes.Ok, string.Join(' ', args) + ": " + run.Stdout + run.Stderr);
        return run.Result();
    }

    /// <summary>Scans and analyses the source, compares it with the library, builds bundles, decides targets, accepts every finding.</summary>
    private async Task<(string Job, long Library)> PrepareAsync()
    {
        var job = (await OkAsync("eval", _corpus, "--stub", Path.Combine(_corpus, EvalTruth.FileName))).GetProperty("job_id").GetInt64().ToString();
        var library = (await OkAsync("scan", _library, "--role", "library")).GetProperty("job").GetProperty("id").GetInt64();
        await OkAsync("compare", job, "--against", library.ToString());
        await OkAsync("bundles", "build", job);

        var targets = Path.Combine(_dir, "targets.json");
        File.WriteAllText(targets, """
            { "schema_version": 1, "kind": "sfo_target_structure", "root": null, "source_job": null,
              "created_at": "2026-11-13T10:00:00Z", "origin": "rule", "model": null,
              "targets": [
                { "id": "photos", "path": "Fotos", "pattern": null, "holds": ["image"], "reason": null, "examples": [] },
                { "id": "documents", "path": "Dokumente", "pattern": null, "holds": ["document"], "reason": null, "examples": [] } ] }
            """);
        await OkAsync("decide", job, "--targets", targets);

        var findings = await OkAsync("findings", job, "--limit", "200");
        var keys = findings.GetProperty("items").EnumerateArray().Select(f => Str(f, "key")!).ToList();
        Assert.NotEmpty(keys);
        await OkAsync(["findings", "accept", job, .. keys]);
        return (job, library);
    }

    private async Task<List<JsonElement>> ActionsAsync(string planId) =>
        (await OkAsync("plan", "show", planId, "--limit", "500")).GetProperty("items").EnumerateArray().ToList();

    private static List<JsonElement> Items(JsonElement result) => result.GetProperty("items").EnumerateArray().ToList();

    private static long Id(JsonElement a) => a.TryGetProperty("action_id", out var id) ? id.GetInt64() : a.GetProperty("id").GetInt64();

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).GetString();

    private static List<string> Strings(JsonElement e, string name) =>
        e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!).ToList();
}
