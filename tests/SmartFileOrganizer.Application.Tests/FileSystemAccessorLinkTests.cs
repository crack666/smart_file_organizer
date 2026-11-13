using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Infrastructure;
using SmartFileOrganizer.Tests.Support;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// Links, file sizes and "same file?" of the real file system accessor
/// (https://github.com/crack666/smart_file_organizer/issues/57). Everything happens in a temp folder the
/// test makes; links are removed as links before the folder is deleted.
/// </summary>
public class FileSystemAccessorLinkTests : IDisposable
{
    private readonly string _dir;
    private readonly FileSystemAccessor _fs = new();

    public FileSystemAccessorLinkTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sfo_links_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_dir, "outside", "Microsoft"));
        Directory.CreateDirectory(Path.Combine(_dir, "profile", "Documents"));
        File.WriteAllText(Path.Combine(_dir, "outside", "Microsoft", "settings.dat"), "program data");
        File.WriteAllText(Path.Combine(_dir, "profile", "Documents", "letter.txt"), "Dear landlord");
    }

    public void Dispose() => FileSystemLinks.DeleteTree(_dir);

    private string P(params string[] parts) => Path.Combine([_dir, .. parts]);

    [Fact]
    public void PlainFolderAndFile_AreNoLinks()
    {
        Assert.Null(_fs.GetLink(P("profile", "Documents")));
        Assert.Null(_fs.GetLink(P("profile", "Documents", "letter.txt")));
        Assert.Null(_fs.GetLink(P("profile", "missing")));
    }

    [LinkFact(LinkNeed.DirectoryLink)]
    public void FolderLink_IsRecognised_WithItsTarget()
    {
        var link = P("profile", "All Users");
        FileSystemLinks.DirectoryLink(link, P("outside"));

        var found = _fs.GetLink(link);

        Assert.NotNull(found);
        Assert.Equal(OperatingSystem.IsWindows() ? LinkKind.Junction : LinkKind.Symlink, found.Kind);
        Assert.Equal(Path.TrimEndingDirectorySeparator(P("outside")), Path.TrimEndingDirectorySeparator(found.Target!), ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal(Path.TrimEndingDirectorySeparator(P("outside")), Path.TrimEndingDirectorySeparator(_fs.ResolveLinkTarget(link)!), ignoreCase: OperatingSystem.IsWindows());

        FileSystemLinks.RemoveLink(link);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(P("outside", "Microsoft", "settings.dat")), "Removing the link must not touch its target.");
    }

    [LinkFact(LinkNeed.FileSymlink)]
    public void FileLink_IsRecognised()
    {
        var link = P("profile", "shortcut.dat");
        FileSystemLinks.FileSymlink(link, P("outside", "Microsoft", "settings.dat"));

        var found = _fs.GetLink(link);

        Assert.NotNull(found);
        Assert.Equal(LinkKind.Symlink, found.Kind);
    }

    [Fact]
    public void GetFileSize_OfAMissingFile_Throws_InsteadOfReadingAsEmpty()
    {
        Assert.Throws<FileNotFoundException>(() => _fs.GetFileSize(P("profile", "gone.txt")));
        Assert.Equal(13, _fs.GetFileSize(P("profile", "Documents", "letter.txt")));
    }

    [LinkFact(LinkNeed.HardLink)]
    public void SameFile_HardLink_IsTheSameFile()
    {
        var original = P("profile", "Documents", "letter.txt");
        var hard = P("profile", "letter-hardlink.txt");
        FileSystemLinks.HardLink(hard, original);

        var result = _fs.SameFile(original, hard);

        Assert.Equal(SameFileAnswer.Same, result.Answer);
    }

    [LinkFact(LinkNeed.DirectoryLink)]
    public void SameFile_ThroughAFolderLink_IsTheSameFile()
    {
        FileSystemLinks.DirectoryLink(P("profile", "Eigene Dateien"), P("profile", "Documents"));

        var result = _fs.SameFile(P("profile", "Documents", "letter.txt"), P("profile", "Eigene Dateien", "letter.txt"));

        Assert.Equal(SameFileAnswer.Same, result.Answer);
    }

    [Fact]
    public void SameFile_SecondSpellingOfThePath_IsTheSameFile()
    {
        var plain = P("profile", "Documents", "letter.txt");
        var detour = Path.Combine(_dir, "profile", "Documents", "..", "Documents", "letter.txt");
        Assert.Equal(SameFileAnswer.Same, _fs.SameFile(plain, detour).Answer);

        if (OperatingSystem.IsWindows())
            Assert.Equal(SameFileAnswer.Same, _fs.SameFile(plain, plain.ToUpperInvariant()).Answer);
    }

    [Fact]
    public void SameFile_ACopyWithEqualContent_IsADifferentFile()
    {
        var original = P("profile", "Documents", "letter.txt");
        var copy = P("profile", "letter-copy.txt");
        File.Copy(original, copy);

        var result = _fs.SameFile(original, copy);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            Assert.Equal(SameFileAnswer.Different, result.Answer);
        else
            Assert.Equal(SameFileAnswer.Unknown, result.Answer);
    }

    [Fact]
    public void SameFile_AMissingPath_IsUnknown_NeverDifferent()
    {
        var result = _fs.SameFile(P("profile", "Documents", "letter.txt"), P("profile", "gone.txt"));

        Assert.Equal(SameFileAnswer.Unknown, result.Answer);
        Assert.Contains("gone.txt", result.Reason);
    }
}
