using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Whether the files of a unit belong together by content (https://github.com/crack666/smart_file_organizer/issues/85):
/// a mixture is split into parts, files that share a concrete feature or a time stay together, and files without a
/// description never split a unit.
/// </summary>
public class ContentUnityTests
{
    private static long _id;

    private static UnityFile File(string name, DateTime when, params (FileFactKey Key, string Value)[] facts) =>
        new(new FileNode { Id = ++_id, Name = name, FullPath = "/src/" + name, Extension = ".jpg", FileType = FileType.Image },
            when, facts.Select(f => new FileFact(f.Key, f.Value, FactSource.Model)).ToList());

    private static readonly (FileFactKey, string)[] Ski =
        [(FileFactKey.Scene, "snowy slope"), (FileFactKey.Activity, "skiing"), (FileFactKey.Setting, "outdoor"), (FileFactKey.Occasion, "holiday")];
    private static readonly (FileFactKey, string)[] Wall =
        [(FileFactKey.Scene, "cracked plaster wall"), (FileFactKey.Activity, "repair work"), (FileFactKey.Setting, "indoor"), (FileFactKey.Occasion, "everyday")];
    private static readonly (FileFactKey, string)[] Interview =
        [(FileFactKey.Scene, "studio with microphone"), (FileFactKey.Activity, "interview"), (FileFactKey.Setting, "indoor"), (FileFactKey.Occasion, "everyday")];

    private static readonly DateTime Day = new(2015, 2, 8, 9, 0, 0);

    [Fact]
    public void DifferentThingsOnTheSameDay_AreParts_EachInTimeOrder()
    {
        var files = new[]
        {
            File("a.jpg", Day, Ski), File("b.jpg", Day.AddMinutes(30), Ski),
            File("c.jpg", Day.AddHours(6), Wall), File("d.jpg", Day.AddHours(6.5), Wall)
        };
        var parts = ContentUnity.Parts(files, LibraryWords.Default, 3);
        Assert.Equal(2, parts.Count);
        Assert.Equal(["a.jpg", "b.jpg"], parts[0].Select(f => f.Name));
        Assert.Equal(["c.jpg", "d.jpg"], parts[1].Select(f => f.Name));
    }

    [Fact]
    public void GeneralFeaturesAlone_DoNotJoin_ButAConcreteOneJoinsAcrossTime()
    {
        // Wall and interview share only "indoor" and "everyday": two parts. The second wall photo, a year later, still
        // joins the first by the wall.
        var files = new[]
        {
            File("wall1.jpg", Day, Wall), File("talk.jpg", Day.AddMinutes(20), Interview), File("wall2.jpg", Day.AddYears(1), Wall)
        };
        var parts = ContentUnity.Parts(files, LibraryWords.Default, 3);
        Assert.Equal(2, parts.Count);
        Assert.Equal(["wall1.jpg", "wall2.jpg"], parts[0].Select(f => f.Name));
        Assert.Equal(["talk.jpg"], parts[1].Select(f => f.Name));
    }

    [Fact]
    public void UndescribedFiles_StayWithTheirNeighbours_AndAloneNeverSplitAUnit()
    {
        var none = ContentUnity.Parts([File("x.jpg", Day), File("y.jpg", Day.AddDays(40)), File("z.jpg", Day.AddDays(80))], LibraryWords.Default, 3);
        Assert.Single(none);
        Assert.Equal(3, none[0].Count);

        // One described file and undescribed ones far away: still one whole, nothing says they differ.
        var one = ContentUnity.Parts([File("x.jpg", Day, Ski), File("y.jpg", Day.AddDays(40))], LibraryWords.Default, 3);
        Assert.Single(one);

        // An undescribed photo between the ski photos joins them by time; the wall photos stay apart.
        var mixed = ContentUnity.Parts(
            [File("s1.jpg", Day, Ski), File("u.jpg", Day.AddMinutes(10)), File("w1.jpg", Day.AddHours(8), Wall), File("w2.jpg", Day.AddHours(8.2), Wall)],
            LibraryWords.Default, 3);
        Assert.Equal(2, mixed.Count);
        Assert.Equal(["s1.jpg", "u.jpg"], mixed[0].Select(f => f.Name));
    }

    [Fact]
    public void DocumentsOfOneIssuer_AreOneWhole()
    {
        (FileFactKey, string)[] invoice = [(FileFactKey.DocumentKind, "invoice"), (FileFactKey.Issuer, "Stadtwerke Beispiel")];
        var parts = ContentUnity.Parts([File("r1.pdf", Day, invoice), File("r2.pdf", Day.AddMonths(1), invoice), File("r3.pdf", Day.AddMonths(2), invoice)],
            LibraryWords.Default, 3);
        Assert.Single(parts);
    }
}
