using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The hard rules of a plan (https://github.com/crack666/smart_file_organizer/issues/11), one test each, on invented
/// paths below a fake root. Nothing is read from disk.
/// </summary>
public class PlanChecksTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "sfo_plan_rules");
    private static readonly string Source = Path.Combine(Root, "stick");
    private static readonly string Library = Path.Combine(Root, "nas", "photos");
    private static readonly string OtherLibrary = Path.Combine(Root, "nas", "documents");
    private static readonly string Profile = Path.Combine(Root, "profile");
    private static readonly string Managed = Path.Combine(Library, "Managed");

    private static PlanCheckContext Ctx(IReadOnlySet<string>? backedUp = null, Func<string, bool>? exists = null) => new(
        Source,
        new Dictionary<long, string> { [2] = Library },
        [Library, OtherLibrary],
        [Managed, Path.Combine(Source, "PhotoApp")],
        [Profile],
        backedUp ?? new HashSet<string>(),
        exists ?? (_ => false));

    private static PlanAction Move(string source, string? target, PlanSourceKind kind = PlanSourceKind.File) => new()
    {
        Id = 1,
        SourceKind = kind,
        SourcePath = source,
        Kind = PlanActionKind.Move,
        TargetPath = target,
        Origin = PlanActionOrigin.Rule
    };

    private static IReadOnlyList<PlanProblem> Check(PlanAction a, PlanCheckContext? ctx = null) =>
        PlanChecks.Check(a, ctx ?? Ctx(), _ => false);

    private static string S(params string[] parts) => Path.Combine([Source, .. parts]);
    private static string L(params string[] parts) => Path.Combine([Library, .. parts]);

    [Fact]
    public void ATargetBelowALibraryRoot_IsValid() =>
        Assert.Empty(Check(Move(S("a.jpg"), L("2021", "a.jpg"))));

    [Fact]
    public void ATargetOutsideEveryLibraryOfThePlan_IsRefused()
    {
        Assert.Contains(PlanProblem.TargetOutsideLibrary, Check(Move(S("a.jpg"), Path.Combine(Root, "elsewhere", "a.jpg"))));
        // A library job that is not part of the plan is no place either; nor the root itself.
        Assert.Contains(PlanProblem.TargetOutsideLibrary, Check(Move(S("a.jpg"), Path.Combine(OtherLibrary, "a.jpg"))));
        Assert.Contains(PlanProblem.TargetOutsideLibrary, Check(Move(S("a.jpg"), Library)));
        // ".." does not climb out unnoticed.
        Assert.Contains(PlanProblem.TargetOutsideLibrary, Check(Move(S("a.jpg"), L("..", "..", "x", "a.jpg"))));
    }

    [Fact]
    public void ATargetInTheSource_IsRefused() =>
        Assert.Contains(PlanProblem.TargetInSource, Check(Move(S("a.jpg"), S("sorted", "a.jpg"))));

    [Fact]
    public void ATargetInAProtectedPlace_IsRefused_EvenWhenALibraryLiesThere()
    {
        var ctx = Ctx() with { LibraryRoots = new Dictionary<long, string> { [5] = Path.Combine(Profile, "Pictures") } };
        var problems = Check(Move(S("a.jpg"), Path.Combine(Profile, "Pictures", "a.jpg")), ctx);
        Assert.Equal([PlanProblem.TargetProtected], problems);
    }

    [Fact]
    public void TheMachinesOwnProtectedPlaces_CoverTheProfileAndTheSystemDrive()
    {
        var places = new ProtectedPlaces().Get();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Contains(places, p => PlanChecks.IsAtOrBelow(Path.Combine(profile, "Documents", "x.pdf"), p));
        if (OperatingSystem.IsWindows())
        {
            var system = Path.GetPathRoot(Environment.SystemDirectory)!;
            Assert.Contains(places, p => PlanChecks.IsAtOrBelow(Path.Combine(system, "Data", "x.pdf"), p));
        }
        else
        {
            Assert.Contains(places, p => PlanChecks.IsAtOrBelow("/usr/share/x", p));
        }
    }

    [Fact]
    public void ContentALibraryHoldsAlready_IsNeitherMovedNorCopied()
    {
        var file = S("Urlaub", "a.jpg");
        var ctx = Ctx(new HashSet<string> { file });
        Assert.Contains(PlanProblem.AlreadyInLibrary, Check(Move(file, L("a.jpg")), ctx));
        var copy = Move(file, L("a.jpg"));
        copy.Kind = PlanActionKind.Copy;
        Assert.Contains(PlanProblem.AlreadyInLibrary, Check(copy, ctx));
        // A folder with one such file does not move as a whole either.
        Assert.Contains(PlanProblem.AlreadyInLibrary, Check(Move(S("Urlaub"), L("Urlaub"), PlanSourceKind.Folder), ctx));
        Assert.Empty(Check(Move(S("Other"), L("Other"), PlanSourceKind.Folder), ctx));
    }

    [Fact]
    public void LeaveIsTheDefault_AndNeedsNoTarget()
    {
        var a = Move(S("a.jpg"), null);
        Assert.Contains(PlanProblem.NoTarget, Check(a));
        a.Kind = PlanActionKind.Leave;
        Assert.Empty(Check(a));
    }

    [Fact]
    public void ASensitiveFile_NeverGoesToQuarantine_AndIsRemovedOnlyByAPerson()
    {
        var a = Move(S("passwords.txt"), L("q", "passwords.txt"));
        a.Sensitive = true;
        a.Kind = PlanActionKind.Quarantine;
        a.Origin = PlanActionOrigin.Human;
        Assert.Contains(PlanProblem.SensitiveKind, Check(a));
        a.Kind = PlanActionKind.DeleteSource;
        a.Origin = PlanActionOrigin.Agent;
        Assert.Contains(PlanProblem.SensitiveKind, Check(a));
        a.Origin = PlanActionOrigin.Human;
        Assert.DoesNotContain(PlanProblem.SensitiveKind, Check(a));
        a.Kind = PlanActionKind.Move;
        a.Origin = PlanActionOrigin.Agent;
        Assert.Empty(Check(a));
    }

    [Fact]
    public void ALibraryIsNeverASource()
    {
        var a = Move(Path.Combine(OtherLibrary, "a.pdf"), L("a.pdf"));
        Assert.Contains(PlanProblem.SourceInLibrary, Check(a));
        a.Kind = PlanActionKind.Copy;
        Assert.Contains(PlanProblem.SourceInLibrary, Check(a));
    }

    [Fact]
    public void ManagedAreas_NothingInThemIsMovedOrRemoved_ATargetInThemIsMarked()
    {
        var inArea = Move(S("PhotoApp", "a.jpg"), L("a.jpg"));
        Assert.Contains(PlanProblem.SourceInManagedArea, Check(inArea));
        inArea.Kind = PlanActionKind.DeleteSource;
        Assert.Contains(PlanProblem.SourceInManagedArea, Check(inArea));
        inArea.Kind = PlanActionKind.Copy;
        Assert.Empty(Check(inArea));

        var into = Move(S("a.jpg"), Path.Combine(Managed, "2021", "a.jpg"));
        Assert.Empty(Check(into));
        Assert.True(PlanChecks.IntoManagedArea(into, Ctx()));
        Assert.False(PlanChecks.IntoManagedArea(Move(S("a.jpg"), L("a.jpg")), Ctx()));
    }

    [Fact]
    public void TwoActionsWithOneTarget_OrSomethingAlreadyThere_Collide()
    {
        var a = Move(S("x", "a.jpg"), L("a.jpg"));
        var b = Move(S("y", "a.jpg"), L("A.JPG"));
        b.Id = 2;
        var c = Move(S("z", "c.jpg"), L("c.jpg"));
        c.Id = 3;
        var all = PlanChecks.CheckAll([a, b, c], Ctx());
        Assert.Contains(PlanProblem.NameCollision, all[1]);
        Assert.Contains(PlanProblem.NameCollision, all[2]);
        Assert.Empty(all[3]);

        var there = PlanChecks.CheckAll([c], Ctx(exists: p => p.EndsWith("c.jpg", StringComparison.Ordinal)));
        Assert.Equal([PlanProblem.NameCollision], there[3]);
    }

    [Fact]
    public void TheBuilder_GivesNoMoveToBackedUpFiles_AndLeavesWithoutABindableTarget()
    {
        var job = new ScanJob { Id = 1, RootPath = Source, Role = ScanRole.Source };
        var dir = new DirectoryReadRow { Id = 10, FullPath = S("Mixed"), Name = "Mixed", ParentPath = Source, RelativePath = "Mixed", Depth = 1, Verdict = "mixed" };
        var rootDir = new DirectoryReadRow { Id = 9, FullPath = Source, Name = "stick", ParentPath = Root, RelativePath = ".", Depth = 0, Verdict = "mixed" };
        FileValueRow Row(long id, string name) => new(
            new FileNode { Id = id, JobId = 1, FullPath = S("Mixed", name), Name = name, ParentPath = S("Mixed"), RootPath = Source, RelativePath = "Mixed/" + name, Size = 10 },
            new FilePlace(null, null, dir, null, false, true),
            new FileValue(FileWorth.Keep, ResultCertainty.Likely, "model"),
            []);
        var files = new Dictionary<long, FileValueRow> { [1] = Row(1, "a.jpg"), [2] = Row(2, "b.jpg") };
        var finding = new Finding
        {
            Key = "in_noise-0000000001",
            Kind = FindingKind.InNoise,
            Certainty = ResultCertainty.Likely,
            Title = "2 photos among noise in Mixed",
            Reason = "r",
            Action = new FindingAction(PlanActionKind.Move, "Worth keeping, among noise."),
            FileIds = [1, 2],
            State = FindingState.Accepted
        };
        var comparison = new Dictionary<long, FileComparison>
        {
            [1] = new(1, S("Mixed", "a.jpg"), 10, ContentState.BackedUp, 99, L("a.jpg"), 1, null, null, MatchJobId: 2)
        };
        var decisions = new Dictionary<long, FileDecision>
        {
            [2] = new() { JobId = 1, FileNodeId = 2, Worth = RetentionValue.Keep, Target = new TargetProposal("photos", "Fotos", "target_structure", [], []) }
        };

        var draft = PlanBuilder.Build(new PlanInputs(job, [finding], files, [rootDir, dir], comparison, decisions, [], null), DateTime.UtcNow);
        var only = Assert.Single(draft.Actions);
        Assert.Equal(S("Mixed", "b.jpg"), only.SourcePath);
        Assert.Equal(PlanActionKind.Leave, only.Kind);
        Assert.Equal(PlanActionKind.Move, only.Recommended);
        Assert.Contains("--library", only.Note);
        Assert.Contains(draft.Notes, n => n.Contains("1 files of accepted findings are in a library"));

        var library = new ScanJob { Id = 2, RootPath = Library, Role = ScanRole.Library };
        var bound = PlanBuilder.Build(new PlanInputs(job, [finding], files, [rootDir, dir], comparison, decisions, [library], library), DateTime.UtcNow);
        Assert.Equal(L("Fotos", "b.jpg"), Assert.Single(bound.Actions).TargetPath);
    }
}
