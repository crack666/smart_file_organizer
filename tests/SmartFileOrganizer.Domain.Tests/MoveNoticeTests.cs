using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Domain.Tests;

/// <summary>The text format of the move notice (https://github.com/crack666/smart_file_organizer/issues/86).</summary>
public class MoveNoticeTests
{
    [Fact]
    public void ANotice_ReadsBackWhatWasWritten_EvenWithTabsAndBackslashesInNames()
    {
        var at = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
        var entries = new[]
        {
            new MoveNoticeEntry(at, 5, 12, "Bild\tmit Tab.jpg", @"E:\Archiv\Bilder\Bild.jpg"),
            new MoveNoticeEntry(at.AddMinutes(1), 5, 13, "zeile\numbruch.txt", "/lib/x.txt")
        };

        var text = MoveNotice.Render(entries);
        Assert.StartsWith("# sfo-move-notice 1\n", text);
        Assert.Contains("moved\t2026-11-14T10:00:00Z\t5\t12\tBild\\tmit Tab.jpg\tE:\\\\Archiv\\\\Bilder\\\\Bild.jpg\n", text);

        var doc = MoveNotice.Parse(text);
        Assert.NotNull(doc);
        Assert.True(doc.Readable);
        Assert.Equal(entries, doc.Entries);
        // With a byte order mark and Windows line ends, as an editor may save it.
        Assert.Equal(entries, MoveNotice.Parse("\uFEFF" + text.Replace("\n", "\r\n"))!.Entries);
    }

    [Theory]
    [InlineData("my own list")]
    [InlineData("")]
    [InlineData("# sfo-move-notice x")]
    public void AnotherFile_IsNoNotice(string text) => Assert.Null(MoveNotice.Parse(text));

    [Fact]
    public void ANoticeOfAnotherVersion_IsRecognisedButNotRead()
    {
        var doc = MoveNotice.Parse("# sfo-move-notice 2\nsomething new\n");
        Assert.NotNull(doc);
        Assert.False(doc.Readable);
        Assert.Empty(doc.Entries);
        Assert.True(MoveNotice.HasNoticeName("/x/_MOVED-FILES.TXT"));
    }
}
