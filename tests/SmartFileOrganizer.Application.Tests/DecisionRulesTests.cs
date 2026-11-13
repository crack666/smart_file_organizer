using SmartFileOrganizer.Application.Decisions;
using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>The pure rules of stage 2 (https://github.com/crack666/smart_file_organizer/issues/37): worth from facts, targets from a structure.</summary>
public class DecisionRulesTests
{
    private static readonly FilePlace Plain = new(null, null, null, null, false, false);
    private static readonly FilePlace Noise = new(null, null, null, null, true, false);

    private static FileNode File(string name, FileType type, FileClassification? c = null) => new()
    {
        Id = 1, JobId = 1, Name = name, Extension = Path.GetExtension(name), FileType = type, Classification = c
    };

    private static FileClassification Result(FileCategory category, Importance importance) => new()
    {
        Category = category, Importance = importance, Certainty = ResultCertainty.Likely, Origin = ResultOrigin.Model
    };

    private static FileFact Model(FileFactKey key, string value) => new(key, value, FactSource.Model);

    [Fact]
    public void APaperAboutThePerson_IsKept_EvenWhenTheSameAnswerCalledItUnimportant()
    {
        var contract = File("vertrag.pdf", FileType.Document, Result(FileCategory.Document, Importance.Low));
        Assert.Equal(FileWorth.Disposable, FindingRules.ValueOf(contract, Plain).Worth);

        var value = DecisionRules.ValueOf(contract, Plain, [Model(FileFactKey.DocumentKind, "contract")]);
        Assert.Equal(new FileValue(FileWorth.Keep, ResultCertainty.Uncertain, "facts"), value);
        // A manual stays what the result said.
        Assert.Equal(FileWorth.Disposable, DecisionRules.ValueOf(contract, Plain, [Model(FileFactKey.DocumentKind, "manual")]).Worth);
    }

    [Fact]
    public void APictureWithPeopleOrOfAnOccasion_IsKept_AScreenshotIsNot()
    {
        var shot = File("IMG_1.jpg", FileType.Image, Result(FileCategory.Screenshot, Importance.Low));
        Assert.Equal(FileWorth.None, FindingRules.ValueOf(shot, Plain).Worth);
        Assert.Equal(new FileValue(FileWorth.Keep, ResultCertainty.Likely, "facts"),
            DecisionRules.ValueOf(shot, Plain, [Model(FileFactKey.People, "true")]));
        Assert.Equal(FileWorth.Keep, DecisionRules.ValueOf(shot, Plain, [Model(FileFactKey.Occasion, "celebration")]).Worth);
        Assert.Equal(FileWorth.None, DecisionRules.ValueOf(shot, Plain, [Model(FileFactKey.Occasion, "everyday")]).Worth);
        Assert.Equal(FileWorth.None, DecisionRules.ValueOf(shot, Plain,
            [Model(FileFactKey.People, "true"), new FileFact(FileFactKey.ImageOrigin, "screenshot", FactSource.Rule)]).Worth);
        // Among noise (a messenger cache) a picture with people is kept, but unsure.
        Assert.Equal(ResultCertainty.Uncertain, DecisionRules.ValueOf(shot, Noise, [Model(FileFactKey.People, "true")]).Certainty);
    }

    [Fact]
    public void APersonsOverride_AlwaysStands()
    {
        var f = File("vertrag.pdf", FileType.Document);
        f.Override = new UserOverride { OverriddenCategory = FileCategory.TrashCandidate };
        var value = DecisionRules.ValueOf(f, Plain, [Model(FileFactKey.DocumentKind, "contract")]);
        Assert.Equal(new FileValue(FileWorth.Disposable, ResultCertainty.Certain, "human"), value);
        Assert.Equal([new DecisionInput("category", "trash_candidate", "human")], DecisionRules.BasisOf(f, Plain, value, []));
    }

    [Fact]
    public void TheBasisNamesTheInputs_TheResultAndTheKeepingFact()
    {
        var contract = File("vertrag.pdf", FileType.Document, Result(FileCategory.Document, Importance.Low));
        var facts = new[] { Model(FileFactKey.DocumentKind, "contract"), Model(FileFactKey.Issuer, "Meier") };
        var decision = DecisionRules.Decide(new FileValueRow(contract, Plain, DecisionRules.ValueOf(contract, Plain, facts), facts), null, DateTime.UtcNow);
        Assert.Equal(RetentionValue.Keep, decision.Worth);
        Assert.Equal("facts", decision.Rule);
        Assert.Equal(
        [
            new DecisionInput("document_kind", "contract", "model"),
            new DecisionInput("category", "document", "model"),
            new DecisionInput("importance", "low", "model")
        ], decision.Basis);
        Assert.Null(decision.Target);
    }

    [Fact]
    public void AMarkedFile_RestsOnItsMarkerOnly()
    {
        var f = File("kontoauszug.pdf", FileType.Document, Result(FileCategory.PersonalDocument, Importance.High));
        f.SensitiveMarkers = SensitiveMarker.Finance;
        f.SensitiveRule = "word:kontoauszug";
        var facts = new[] { Model(FileFactKey.Issuer, "Bank") };
        var proposer = new TargetStructureProposer(Structure(new TargetEntry("docs", "Docs", "{issuer}", ["document"], null, [])));
        var decision = DecisionRules.Decide(new FileValueRow(f, Plain, DecisionRules.ValueOf(f, Plain, facts), facts), proposer, DateTime.UtcNow);
        Assert.Equal(RetentionValue.Keep, decision.Worth);
        Assert.Equal(ResultCertainty.Likely, decision.Certainty);
        Assert.Equal([new DecisionInput("sensitive", "finance", "word:kontoauszug")], decision.Basis);
        Assert.Null(decision.Target);
    }

    private static TargetStructure Structure(params TargetEntry[] targets) =>
        new(TargetStructureFile.SchemaVersion, TargetStructureFile.KindValue, null, null, DateTime.UtcNow, AdviceOrigin.Rule, null, targets);

    [Fact]
    public void TheTarget_IsTheFirstThatHoldsTheType_ItsPatternFilledFromTheFacts()
    {
        var proposer = new TargetStructureProposer(Structure(
            new TargetEntry("photos", "Fotos", "{Anlass} {Jahr}", ["image", "video"], null, []),
            new TargetEntry("more_photos", "Other", null, ["image"], null, []),
            new TargetEntry("docs", "Dokumente", "{Bereich}/{Aussteller}", ["document"], null, [])));

        var photo = proposer.Propose(File("a.jpg", FileType.Image),
            [Model(FileFactKey.Occasion, "holiday"), new FileFact(FileFactKey.TakenAt, "2019-07-14T10:22:33")])!;
        Assert.Equal("photos", photo.TargetId);
        Assert.Equal("Fotos/holiday 2019", photo.Path);
        Assert.Equal(
        [
            new DecisionInput("file_type", "image", "scan"),
            new DecisionInput("occasion", "holiday", "model"),
            new DecisionInput("taken_at", "2019-07-14T10:22:33", "metadata")
        ], photo.Basis);
        Assert.Empty(photo.Unresolved);

        // "No occasion" names no folder; the level stays open.
        var noOccasion = proposer.Propose(File("b.jpg", FileType.Image), [Model(FileFactKey.Occasion, "none")])!;
        Assert.Equal("Fotos", noOccasion.Path);
        Assert.Equal(["Anlass", "Jahr"], noOccasion.Unresolved);

        // A value with a separator stays one folder.
        var doc = proposer.Propose(File("c.pdf", FileType.Document),
            [Model(FileFactKey.DocumentKind, "invoice"), Model(FileFactKey.Issuer, "Strom/Gas AG")])!;
        Assert.Equal("Dokumente/invoice/Strom-Gas AG", doc.Path);

        Assert.Null(proposer.Propose(File("d.mp3", FileType.Audio), []));
    }
}
