using SmartFileOrganizer.Scanning.Engine;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Domain.Tests;

public class FileTypeDetectorTests
{
    [Theory]
    [InlineData(".jpg",  FileType.Image)]
    [InlineData(".PNG",  FileType.Image)]
    [InlineData(".mp4",  FileType.Video)]
    [InlineData(".pdf",  FileType.Document)]
    [InlineData(".zip",  FileType.Archive)]
    [InlineData(".exe",  FileType.Executable)]
    [InlineData(".cs",   FileType.Code)]
    [InlineData(".mp3",  FileType.Audio)]
    [InlineData(".xyz",  FileType.Other)]
    [InlineData("",      FileType.Unknown)]
    public void Detect_ReturnsExpectedType(string extension, FileType expected)
    {
        Assert.Equal(expected, FileTypeDetector.Detect(extension));
    }
}
