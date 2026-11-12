using SmartFileOrganizer.Application.Sensitivity;

namespace SmartFileOrganizer.Application.Tests;

public class DirectorySummaryGuardTests
{
    [Fact]
    public void SummaryWithoutModel_IsAlwaysCurrent() =>
        Assert.True(DirectorySummaryGuard.IsCurrent(null, null, ["passwords.txt"]));

    [Fact]
    public void ModelSummary_WithoutMarkedFiles_IsCurrent() =>
        Assert.True(DirectorySummaryGuard.IsCurrent("m", null, []));

    [Fact]
    public void ModelSummary_WithoutRecordedInputs_IsStale_OnceAFileIsMarked() =>
        Assert.False(DirectorySummaryGuard.IsCurrent("m", null, ["passwords.txt"]));

    [Fact]
    public void ModelSummary_IsCurrent_WhenEveryMarkedFileEnteredAsItsNeutralLine()
    {
        var inputs = DirectorySummaryGuard.FormatInputs(["Passwords.txt", "id_ed25519"]);
        Assert.True(DirectorySummaryGuard.IsCurrent("m", inputs, ["passwords.txt"]));
        Assert.False(DirectorySummaryGuard.IsCurrent("m", inputs, ["passwords.txt", "notes-2009.txt"]));
    }

    [Fact]
    public void UnreadableInputs_CountAsUnknown() =>
        Assert.False(DirectorySummaryGuard.IsCurrent("m", "not json", ["a.txt"]));

    [Fact]
    public void FormatInputs_EmptyList_IsAnEmptyArray() =>
        Assert.Equal("[]", DirectorySummaryGuard.FormatInputs([]));
}
