using SmartFileOrganizer.Cli.Contract;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// The published result schemas of <c>files</c>, <c>dirs</c>, <c>show</c> and <c>summary</c> under <c>schemas/cli/v1/</c>
/// (https://github.com/crack666/smart_file_organizer/issues/116) are the ones <see cref="CliSchemas"/> generates from the
/// result records. After a deliberate change: <c>SFO_WRITE_SCHEMAS=1 dotnet test --filter CliSchemaTests</c>.
/// </summary>
public class CliSchemaTests
{
    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SmartFileOrganizer.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Repository root (SmartFileOrganizer.slnx) not found above the test output.");
    }

    internal static string Published(string file) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. CliSchemas.Folder, file]));

    public static TheoryData<string> Commands() => [.. CliSchemas.Commands.Select(c => c.Command)];

    [Theory]
    [MemberData(nameof(Commands))]
    public void PublishedSchema_IsTheGeneratedOne(string command)
    {
        var file = CliSchemas.Commands.Single(c => c.Command == command).File;
        var generated = CliSchemas.Generate(command);
        var path = Path.Combine([RepoRoot(), .. CliSchemas.Folder, file]);

        if (Environment.GetEnvironmentVariable("SFO_WRITE_SCHEMAS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, generated);
        }

        Assert.True(File.Exists(path), $"{path} is missing; write it with SFO_WRITE_SCHEMAS=1.");
        Assert.Equal(generated, File.ReadAllText(path).ReplaceLineEndings("\n"));
    }
}
