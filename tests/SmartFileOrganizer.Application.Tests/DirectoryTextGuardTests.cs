using SmartFileOrganizer.Application.Sensitivity;

namespace SmartFileOrganizer.Application.Tests;

public class DirectoryTextGuardTests
{
    private static readonly string[] Marked = ["passwords.kdbx", "id_rsa"];

    [Theory]
    [InlineData("Holds passwords.kdbx and photos.", true)]   // full name
    [InlineData("A PASSWORDS vault and more.", true)]          // stem, case-insensitive
    [InlineData("SSH material: id_rsa.", true)]
    [InlineData("Holiday photos from 2019.", false)]
    public void NamesAny_FindsNameOrStem(string text, bool expected)
        => Assert.Equal(expected, DirectoryTextGuard.NamesAny(text, Marked));

    [Fact]
    public void ShortStems_AreNotMatchedAlone()
        => Assert.False(DirectoryTextGuard.NamesAny("a db of things", ["db.sqlite"]));

    [Fact]
    public void Protect_ReplacesNamingText_UnlessIncludeSensitive()
    {
        var hidden = DirectoryTextGuard.Protect("Keeps id_rsa.", Marked, null, out var withheld);
        Assert.True(withheld);
        Assert.Equal(DirectoryTextGuard.NeutralHint, hidden);

        var shown = DirectoryTextGuard.Protect("Keeps id_rsa.", Marked,
            new SensitiveOutputOptions { IncludeSensitive = true }, out withheld);
        Assert.False(withheld);
        Assert.Equal("Keeps id_rsa.", shown);

        Assert.Equal("Photos.", DirectoryTextGuard.Protect("Photos.", Marked, null, out withheld));
        Assert.False(withheld);
    }
}
