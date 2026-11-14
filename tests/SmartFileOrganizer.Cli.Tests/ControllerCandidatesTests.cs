using System.Text.Json;

namespace SmartFileOrganizer.Cli.Tests;

/// <summary>
/// What the first real run of the controller workflow showed (https://github.com/crack666/smart_file_organizer/issues/97):
/// the top level of a library root scanned with a trailing separator, weak candidates, the name of a source in an inbox,
/// and the size cap of the candidates. Same generated library and source as <see cref="FindingTargetsCommandTests"/>.
/// </summary>
public partial class FindingTargetsCommandTests
{
    private static List<string?> Names(JsonElement list) => list.EnumerateArray().Select(s => s.GetString()).ToList();

    /// <summary>
    /// Done when: the top level of a library shows whether its root was stored with or without a trailing separator, in a
    /// structure-only scan and in a full one. Before, <c>library folder &lt;job&gt; ""</c> listed no subfolders for a root
    /// scanned as <c>...\share\</c>.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task LibraryFolder_ListsTheTopLevel_WithOrWithoutTrailingSeparatorAtTheRoot(bool trailing, bool structureOnly)
    {
        var root = trailing ? _library + Path.DirectorySeparatorChar : _library;
        string[] scan = structureOnly ? ["scan", root, "--role", "library", "--structure-only"] : ["scan", root, "--role", "library"];
        var job = (await OkAsync(null, scan)).GetProperty("job");
        Assert.Equal(trailing, job.GetProperty("root_path").GetString()!.EndsWith(Path.DirectorySeparatorChar));
        var library = job.GetProperty("id").GetInt64().ToString();

        foreach (var path in new[] { "", ".", "/", root, _library })
        {
            var top = await OkAsync("library", "folder", library, path);
            Assert.Equal(["Dokumente", "Fotos"], Names(top.GetProperty("subfolders")));
            Assert.Equal(2, top.GetProperty("folder").GetProperty("subdirs").GetInt32());
            Assert.Equal(12, top.GetProperty("folder").GetProperty("files").GetInt64());
        }
        var photos = await OkAsync("library", "folder", library, "Fotos");
        Assert.Equal(["Grillen 2016", "Skifahrt 2015"], Names(photos.GetProperty("subfolders")));
    }
}
