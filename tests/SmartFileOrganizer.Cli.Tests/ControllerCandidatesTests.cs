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

    /// <summary>
    /// Done when: a candidate that fits only by period and kind of files (a broad folder of photos from many years) is no
    /// candidate of equal rank but listed apart under <c>weak_candidates</c>, without what the folder holds; the ski trip
    /// folder, whose short period holds the photos exactly, stays a candidate. The top level of the library, with counts,
    /// kinds of files and period, is in the head of the answer once.
    /// </summary>
    [Fact]
    public async Task Candidates_KeepPeriodAndKindOnlyCandidatesApart_AndListTheTopLevelOfEachLibrary()
    {
        // A broad folder: photos from 2010 to 2020, one per year.
        for (var year = 2010; year <= 2020; year++)
            Write(_library, $"Bilder/IMG_{year}0601_120000.jpg", $"lib {year}", new DateTime(year, 6, 1, 12, 0, 0));
        var (library, source) = await PrepareAsync();
        var job = source.ToString();
        var key = Str(PhotoBrief(await OkAsync("findings", job, "--summary")), "key");

        var candidates = await OkAsync("findings", "candidates", job, "--limit", "20");
        var group = Candidate(candidates, key).GetProperty("groups")[0];
        var strong = group.GetProperty("candidates").EnumerateArray().ToList();
        var weak = group.GetProperty("weak_candidates").EnumerateArray().ToList();

        Assert.Contains(strong, c => Str(c, "destination_relative") == "Fotos/Skifahrt 2015");
        Assert.DoesNotContain(strong, c => Str(c, "destination_relative").StartsWith("Bilder", StringComparison.Ordinal));
        Assert.All(strong, c => Assert.Contains(c.GetProperty("agrees").EnumerateArray(),
            a => a.GetString() is not ("period" or "file_type") || Str(c, "destination_relative") == "Fotos/Skifahrt 2015"));
        var broad = weak.Single(c => Str(c, "destination_relative") == "Bilder");
        Assert.Equal(library, broad.GetProperty("library_job_id").GetInt64());
        Assert.Equal(11, broad.GetProperty("files").GetInt64());
        Assert.All(broad.GetProperty("agrees").EnumerateArray(), a => Assert.Contains(a.GetString(), new[] { "period", "file_type" }));
        Assert.False(broad.TryGetProperty("folder", out _));
        Assert.DoesNotContain(weak, c => Str(c, "destination_relative") == "Fotos/Skifahrt 2015");

        // The top level, once per library.
        var head = candidates.GetProperty("libraries").EnumerateArray().Single();
        Assert.False(head.GetProperty("root_sensitive").GetBoolean());
        var top = head.GetProperty("top_level").EnumerateArray().ToList();
        Assert.Equal(["Bilder", "Dokumente", "Fotos"], top.Select(t => Str(t, "name")).ToList());
        var pictures = top.Single(t => Str(t, "name") == "Bilder");
        Assert.Equal(11, pictures.GetProperty("files").GetInt64());
        Assert.Equal(11, pictures.GetProperty("by_file_type").GetProperty("image").GetInt64());
        Assert.StartsWith("2010-06-01", Str(pictures.GetProperty("period"), "from"));
        Assert.StartsWith("2020-06-01", Str(pictures.GetProperty("period"), "to"));
        var fotos = top.Single(t => Str(t, "name") == "Fotos");
        Assert.Equal(2, fotos.GetProperty("subdirs").GetInt32());
        Assert.Equal(9, fotos.GetProperty("files").GetInt64());
        Assert.False(fotos.GetProperty("sensitive").GetBoolean());
    }
}
