using SmartFileOrganizer.Application.Findings;
using SmartFileOrganizer.Domain.Models;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>The name of a source in an inbox (https://github.com/crack666/smart_file_organizer/issues/97). Invented paths and labels.</summary>
public class InboxNamesTests
{
    private static readonly string DriveRoot = OperatingSystem.IsWindows() ? @"E:\" : "/";
    private static readonly string Folder = OperatingSystem.IsWindows() ? @"E:\DCIM" : "/media/stick/DCIM";

    private static ScanJob Job(string root) => new() { Id = 7, RootPath = root };

    [Fact]
    public void ADriveRoot_IsNamedAfterItsLabel()
    {
        Assert.Equal("Urlaub-Stick", InboxNames.Of(Job(DriveRoot), "Urlaub-Stick"));
    }

    [Fact]
    public void AFolderOnADrive_KeepsItsNameAfterTheLabel()
    {
        Assert.Equal("Urlaub-Stick - DCIM", InboxNames.Of(Job(Folder), "Urlaub-Stick"));
        Assert.Equal("dcim", InboxNames.Of(Job(Folder), "dcim"));   // label and folder name alike: once
    }

    [Fact]
    public void WithoutALabel_TheFolderNameOrTheJob()
    {
        Assert.Equal("DCIM", InboxNames.Of(Job(Folder), null));
        Assert.Equal("DCIM", InboxNames.Of(Job(Folder), "  "));
        Assert.Equal("source-7", InboxNames.Of(Job(DriveRoot), null));
    }

    [Fact]
    public void ALabel_LosesWhatNoFolderNameCanHold()
    {
        Assert.Equal("A_B_C", InboxNames.Of(Job(DriveRoot), "A:B*C."));
    }
}
