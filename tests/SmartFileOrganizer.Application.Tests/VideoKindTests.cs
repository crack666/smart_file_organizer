using SmartFileOrganizer.Application.Media;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Enums;
using SmartFileOrganizer.Domain.Models;
using SmartFileOrganizer.Infrastructure.Content;
using SmartFileOrganizer.Infrastructure.Evaluation;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Application.Tests;

/// <summary>
/// The kind and origin of a video from container, name and companions, without a model
/// (https://github.com/crack666/smart_file_organizer/issues/103). Containers are written by <see cref="SyntheticVideo"/>;
/// names follow the schemes of camera makers and programs, titles are invented.
/// </summary>
public class VideoKindTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sfo_video_{Guid.NewGuid():N}");

    public VideoKindTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── names ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GX010042.MP4", "own_recording", "action_cam", "certain")]
    [InlineData("GOPR0815.MP4", "own_recording", "action_cam", "certain")]
    [InlineData("VID_20230716_183001_00_012.insv", "own_recording", "camera_360", "certain")]
    [InlineData("DJI_0042.MP4", "own_recording", "drone", "likely")]
    [InlineData("VID_20230716_183001.mp4", "own_recording", "phone", "likely")]
    [InlineData("PXL_20230716_183001123.mp4", "own_recording", "phone", "likely")]
    [InlineData("MVI_1234.MOV", "own_recording", "camera", "likely")]
    [InlineData("Screen Recording 2024-05-01 at 18.30.12.mov", "screen_recording", null, "likely")]
    [InlineData("2024-05-01 18-30-12.mkv", "screen_recording", null, "likely")]
    [InlineData("Example Game 2024.05.01 - 18.30.12.02.DVR.mp4", "screen_recording", null, "likely")]
    [InlineData("VID-20230716-WA0003.mp4", "web_clip", null, "likely")]
    [InlineData("How to fold a paper boat [aB3dE5fG7hJ].webm", "web_clip", null, "likely")]
    [InlineData("Example Band - Example Song (Official Video).mp4", "music_video", null, "likely")]
    [InlineData("Some.Invented.Film.2009.1080p.BluRay.x264-GROUP.mkv", "movie", null, "certain")]
    [InlineData("Some Invented Film (2009).mkv", "movie", null, "likely")]
    [InlineData("Example.Show.S02E05.720p.WEB-DL.mkv", "series_episode", null, "certain")]
    [InlineData("Example Show 1x05.avi", "series_episode", null, "certain")]
    [InlineData("Geburtstag_Opa_2018.mp4", "unknown", null, "uncertain")]
    [InlineData("Holiday 2019 1080p.mp4", "unknown", null, "uncertain")]
    [InlineData("clip.mp4", "unknown", null, "uncertain")]
    public void Name_DecidesKindAndDevice(string name, string kind, string? device, string certainty)
    {
        var origin = VideoKindRules.Decide(null, name, "Videos", VideoCompanions.None);
        Assert.Equal(kind, origin.Kind.ToCode());
        Assert.Equal(device, origin.Device?.ToCode());
        Assert.Equal(certainty, origin.KindCertainty.ToCode());
    }

    [Fact]
    public void ReleaseName_GivesTitleAndYear_EpisodeGivesSeasonAndEpisode()
    {
        var film = VideoKindRules.Decide(null, "Some.Invented.Film.2009.1080p.BluRay.x264-GROUP.mkv", "", VideoCompanions.None);
        Assert.Equal(("Some Invented Film", 2009), (film.Title, film.Year));

        var episode = VideoKindRules.Decide(null, "Example.Show.S02E05.720p.WEB-DL.mkv", "", VideoCompanions.None);
        Assert.Equal(("Example Show", 2, 5), (episode.Title, episode.Season, episode.Episode));

        // The series title from the folder above the season folder when the name has none.
        var bare = VideoKindRules.Decide(null, "S01E03.mkv", "Serien/Example Show/Staffel 1", VideoCompanions.None);
        Assert.Equal(("Example Show", 1, 3), (bare.Title, bare.Season, bare.Episode));
    }

    [Fact]
    public void Companions_RaiseOrDecide()
    {
        // A film name with brackets is likely; artwork of a media library next to it makes it certain.
        var withArt = VideoKindRules.Decide(null, "Some Invented Film (2009).mkv", "Filme", new VideoCompanions(["poster.jpg"], Artwork: true));
        Assert.Equal((VideoKind.Movie, ResultCertainty.Certain, FactSource.Companion), (withArt.Kind, withArt.KindCertainty, withArt.KindSource));

        // An info file decides over the name.
        var nfo = VideoKindRules.Decide(null, "clip.mp4", "", new VideoCompanions(["clip.nfo"], InfoRoot: "episodedetails", InfoTitle: "Example Show", InfoSeason: 3, InfoEpisode: 7));
        Assert.Equal((VideoKind.SeriesEpisode, ResultCertainty.Certain, 3, 7), (nfo.Kind, nfo.KindCertainty, nfo.Season, nfo.Episode));

        // DJI telemetry as subtitles confirms the drone.
        var drone = VideoKindRules.Decide(null, "DJI_0042.MP4", "", new VideoCompanions(["DJI_0042.SRT"], TelemetrySubtitles: true));
        Assert.Equal((RecordingDevice.Drone, ResultCertainty.Certain, FactSource.Companion), (drone.Device, drone.KindCertainty, drone.KindSource));
    }

    [Fact]
    public void NothingKnown_LeavesTheModelTheKindsTheDurationAllows()
    {
        var shortClip = VideoKindRules.Decide(new VideoContainerInfo { DurationSeconds = 45 }, "clip.mp4", "", VideoCompanions.None);
        Assert.False(shortClip.IsSettled);
        Assert.DoesNotContain(VideoKind.Movie, shortClip.Remaining!);
        Assert.DoesNotContain(VideoKind.SeriesEpisode, shortClip.Remaining!);
        Assert.Contains(VideoKind.OwnRecording, shortClip.Remaining!);
        Assert.DoesNotContain(VideoKind.Unknown, shortClip.Remaining!);
    }

    [Theory]
    [InlineData("GoPro", "HERO12 Black", "action_cam")]
    [InlineData("Apple", "iPhone 15", "phone")]
    [InlineData("samsung", "SM-S918B", "phone")]
    [InlineData("DJI", "FC3411", "drone")]
    [InlineData("DJI", "Osmo Action 4", "action_cam")]
    [InlineData("Insta360", "X3", "camera_360")]
    [InlineData("Sony", "ILCE-7M4", "camera")]
    public void MakeAndModel_NameTheDevice(string make, string model, string device)
    {
        var origin = VideoKindRules.Decide(new VideoContainerInfo { Make = make, Model = model }, "clip.mp4", "", VideoCompanions.None);
        Assert.Equal((VideoKind.OwnRecording, FactSource.Metadata, ResultCertainty.Certain), (origin.Kind, origin.KindSource, origin.KindCertainty));
        Assert.Equal(device, origin.Device?.ToCode());
    }

    // ── containers ───────────────────────────────────────────────────────

    [Fact]
    public void Mp4_UserData_TracksAndChapters_AreRead()
    {
        var path = Write("a.mp4", SyntheticVideo.Mp4(new SyntheticVideoSpec
        {
            Make = "GoPro", Model = "HERO12 Black", DurationSeconds = 90, Width = 2704, Height = 1520, FrameRate = 50,
            AudioLanguages = ["eng"], SubtitleLanguages = ["ger"], Chapters = 3, Location = true, Software = "Example Editor 2.0"
        }));

        var info = VideoContainerReader.Read(path)!;
        Assert.Equal(("GoPro", "HERO12 Black", "Example Editor 2.0"), (info.Make, info.Model, info.Software));
        Assert.Equal((2704, 1520), (info.Width, info.Height));
        Assert.Equal(90, info.DurationSeconds!.Value, 1);
        Assert.Equal(50, info.FrameRate!.Value, 0);
        Assert.True(info.HasLocation);
        Assert.Equal(3, info.ChapterCount);
        Assert.Equal(["video", "audio", "subtitle"], info.Tracks.Select(t => t.Type));
        Assert.Equal(["eng", "ger"], info.Tracks.Skip(1).Select(t => t.Language));
    }

    [Fact]
    public void Mp4_PhoneMetadataKeys_AreRead()
    {
        var path = Write("b.mov", SyntheticVideo.Mp4(new SyntheticVideoSpec { Make = "Apple", Model = "iPhone 15", AsMetadataKeys = true, Location = true }));
        var info = VideoContainerReader.Read(path)!;
        Assert.Equal(("Apple", "iPhone 15"), (info.Make, info.Model));
        Assert.True(info.HasLocation);
    }

    [Fact]
    public void Matroska_InfoTracksChaptersTags_AreRead()
    {
        var path = Write("c.mkv", SyntheticVideo.Mkv(new SyntheticVideoSpec
        {
            Software = "OBS Studio 30.1", Title = "Recording", DurationSeconds = 600, Width = 2560, Height = 1440, FrameRate = 60,
            AudioLanguages = ["ger", "eng"], SubtitleLanguages = ["eng"], Chapters = 4
        }));

        var info = VideoContainerReader.Read(path)!;
        Assert.Equal("matroska", info.Format);
        Assert.Equal("OBS Studio 30.1", info.Software);
        Assert.Equal((2560, 1440), (info.Width, info.Height));
        Assert.Equal(600, info.DurationSeconds!.Value, 1);
        Assert.Equal(60, info.FrameRate!.Value, 1);
        Assert.Equal(4, info.ChapterCount);
        Assert.Equal(["ger", "eng"], info.Tracks.Where(t => t.Type == "audio").Select(t => t.Language));
    }

    [Fact]
    public void BrokenOrForeignContainer_FallsBackToName()
    {
        var path = Write("Example.Show.S01E02.mkv", [0x1A, 0x45, 0xDF, 0xA3, 0x99, 0x00, 0x01]);
        var reading = new VideoOriginReader().Read(Node(path))!;
        Assert.Equal(VideoKind.SeriesEpisode, reading.Origin.Kind);
    }

    // ── the five cases of the issue, through the preparer ────────────────

    [Fact]
    public async Task FiveCases_FactsAreRight_AndOnlyTheOpenOneAsksTheModel()
    {
        var camera = Node(Write("clip_a.mp4", SyntheticVideo.Mp4(new SyntheticVideoSpec { Make = "GoPro", Model = "HERO12 Black", Location = true })));
        var recorder = Node(Write("clip_b.mkv", SyntheticVideo.Mkv(new SyntheticVideoSpec { Software = "OBS Studio 30.1" })));
        var film = Node(Write("Some.Invented.Film.2009.1080p.BluRay.x264-GROUP.mkv", SyntheticVideo.Mkv(new SyntheticVideoSpec { DurationSeconds = 5400, AudioLanguages = ["ger", "eng"] })));
        var episode = Node(Write("Example.Show.S02E05.mkv", SyntheticVideo.Mkv(new SyntheticVideoSpec { DurationSeconds = 1500 })));
        var nothing = Node(Write("clip_c.mp4", SyntheticVideo.Mp4(new SyntheticVideoSpec { DurationSeconds = 40 })));

        var preparer = new AiClassificationInputPreparer(new FileContentExtractor(), new OllamaOptions(), new VideoFrameExtractor(() => null));
        string? Fact(OllamaClassificationInput i, FileFactKey k) => i.Facts.FirstOrDefault(f => f.Key == k)?.Value;

        var a = await preparer.PrepareAsync(camera);
        Assert.Equal("own_recording", Fact(a, FileFactKey.VideoKind));
        Assert.Equal("action_cam", Fact(a, FileFactKey.RecordingDevice));
        Assert.Equal("GoPro HERO12 Black", Fact(a, FileFactKey.Device));
        Assert.Equal(FactSource.Metadata, a.Facts.Single(f => f.Key == FileFactKey.VideoKind).Source);
        Assert.Equal(ResultCertainty.Certain, a.Facts.Single(f => f.Key == FileFactKey.VideoKind).Certainty);
        Assert.Null(a.VideoKindChoices);
        Assert.Null(a.RuleResult); // described by the model, not asked for the kind
        Assert.Null(OllamaService.VideoKindsOf(a));

        var b = await preparer.PrepareAsync(recorder);
        Assert.Equal("screen_recording", Fact(b, FileFactKey.VideoKind));
        Assert.Equal("OBS Studio 30.1", Fact(b, FileFactKey.Encoder));
        Assert.Null(OllamaService.VideoKindsOf(b));

        var c = await preparer.PrepareAsync(film);
        Assert.Equal("movie", Fact(c, FileFactKey.VideoKind));
        Assert.Equal(("Some Invented Film", "2009"), (Fact(c, FileFactKey.VideoTitle), Fact(c, FileFactKey.ReleaseYear)));
        Assert.Equal("de,en", Fact(c, FileFactKey.AudioLanguages));
        Assert.Equal(FileCategory.Video, c.RuleResult?.Category); // no model call at all
        Assert.Equal("rule:video-kind-movie", c.RuleResult?.ModelUsed);

        var d = await preparer.PrepareAsync(episode);
        Assert.Equal(("series_episode", "2", "5"), (Fact(d, FileFactKey.VideoKind), Fact(d, FileFactKey.SeasonNumber), Fact(d, FileFactKey.EpisodeNumber)));
        Assert.NotNull(d.RuleResult);

        var e = await preparer.PrepareAsync(nothing);
        Assert.Equal("unknown", Fact(e, FileFactKey.VideoKind));
        Assert.Null(e.RuleResult);
        Assert.NotNull(OllamaService.VideoKindsOf(e));
        Assert.DoesNotContain(VideoKind.Movie, e.VideoKindChoices!);
        var schema = System.Text.Json.JsonSerializer.Serialize(OllamaService.BuildClassificationSchema(false, OllamaService.DescriptionOf(e), OllamaService.VideoKindsOf(e)));
        Assert.Contains("\"video_kind\"", schema);
    }

    // ── the model's answer against the rules ─────────────────────────────

    [Fact]
    public void ModelAnswer_FillsAnOpenKind_ButNeverOverridesASureOne()
    {
        FileFact Rule(VideoKind k, ResultCertainty c, FactSource s = FactSource.Metadata) => new(FileFactKey.VideoKind, k.ToCode(), s, c);
        var model = new FileFact(FileFactKey.VideoKind, "web_clip", FactSource.Model, ResultCertainty.Likely);

        var sure = VideoKindMerge.Merge([Rule(VideoKind.OwnRecording, ResultCertainty.Certain)], [model]);
        Assert.Equal("own_recording", sure.Single(f => f.Key == FileFactKey.VideoKind).Value);
        Assert.Equal("web_clip", sure.Single(f => f.Key == FileFactKey.VideoKindDisputed).Value);

        var open = VideoKindMerge.Merge([Rule(VideoKind.Unknown, ResultCertainty.Uncertain, FactSource.Rule)], [model]);
        Assert.Equal((FactSource.Model, "web_clip"), (open.Single(f => f.Key == FileFactKey.VideoKind).Source, open.Single(f => f.Key == FileFactKey.VideoKind).Value));
        Assert.DoesNotContain(open, f => f.Key == FileFactKey.VideoKindDisputed);

        var weak = VideoKindMerge.Merge([Rule(VideoKind.OwnRecording, ResultCertainty.Uncertain)], [model]);
        Assert.Equal("web_clip", weak.Single(f => f.Key == FileFactKey.VideoKind).Value);
        Assert.Equal("own_recording", weak.Single(f => f.Key == FileFactKey.VideoKindDisputed).Value);
    }

    [Fact]
    public void ModelAnswer_IsParsed_OnlyAsAKnownCode()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"video_kind":"own_recording"}""");
        Assert.Contains(OllamaService.ParseDescription(doc.RootElement), f => f.Key == FileFactKey.VideoKind && f.Value == "own_recording" && f.Source == FactSource.Model);
        using var unknown = System.Text.Json.JsonDocument.Parse("""{"video_kind":"unknown"}""");
        Assert.DoesNotContain(OllamaService.ParseDescription(unknown.RootElement), f => f.Key == FileFactKey.VideoKind);
        using var other = System.Text.Json.JsonDocument.Parse("""{"video_kind":"home_movie"}""");
        Assert.DoesNotContain(OllamaService.ParseDescription(other.RootElement), f => f.Key == FileFactKey.VideoKind);
    }

    [Fact]
    public void StoredFacts_OfAMarkedVideo_LoseTheTitle_KeepTheKind()
    {
        var file = new FileNode { Name = "x.mkv", FileType = FileType.Video, SensitiveMarkers = SensitiveMarker.PrivateMessage };
        var facts = ClassificationService.FactsToStore(file, new FileClassification(),
            [new FileFact(FileFactKey.VideoKind, "movie", FactSource.Name, ResultCertainty.Certain), new FileFact(FileFactKey.VideoTitle, "Secret", FactSource.Name)]);
        Assert.Contains(facts, f => f.Key == FileFactKey.VideoKind);
        Assert.DoesNotContain(facts, f => f.Key == FileFactKey.VideoTitle);
    }

    // ── split and mix ────────────────────────────────────────────────────

    [Fact]
    public void Split_SeparatesKnownKinds_AndTheRestGoesWithTheOwnRecordings()
    {
        FileNode File(long id) => new() { Id = id, Name = $"f{id}" };
        IReadOnlyList<FileFact> Kind(VideoKind k, ResultCertainty c = ResultCertainty.Certain) => [new(FileFactKey.VideoKind, k.ToCode(), FactSource.Name, c)];
        var files = Enumerable.Range(1, 6).Select(i => File(i)).ToList();
        var facts = new Dictionary<long, IReadOnlyList<FileFact>>
        {
            [1] = Kind(VideoKind.Movie), [2] = Kind(VideoKind.OwnRecording), [3] = Kind(VideoKind.OwnRecording, ResultCertainty.Likely),
            [4] = Kind(VideoKind.SeriesEpisode), [5] = Kind(VideoKind.Unknown, ResultCertainty.Uncertain)
            // 6: a picture, no kind
        };

        var parts = VideoKindSplit.Parts(files, facts);
        Assert.Equal([[2, 3, 5, 6], [1], [4]], parts.Select(p => p.Select(f => f.Id).ToArray()));

        // One known kind only: one part.
        Assert.Single(VideoKindSplit.Parts(files, new Dictionary<long, IReadOnlyList<FileFact>> { [1] = Kind(VideoKind.Movie) }));
    }

    [Fact]
    public void Mix_CountsKindsAndDevices_AndNamesTheWorks()
    {
        IReadOnlyList<FileFact> Ep(int season, int episode) =>
        [
            new(FileFactKey.VideoKind, "series_episode", FactSource.Name, ResultCertainty.Certain), new(FileFactKey.VideoTitle, "Example Show", FactSource.Name),
            new(FileFactKey.SeasonNumber, season.ToString(), FactSource.Name), new(FileFactKey.EpisodeNumber, episode.ToString(), FactSource.Name)
        ];
        var mix = VideoOriginMix.From(
        [
            Ep(1, 1), Ep(1, 2), Ep(2, 1),
            [new(FileFactKey.VideoKind, "own_recording", FactSource.Metadata, ResultCertainty.Certain), new(FileFactKey.RecordingDevice, "drone", FactSource.Metadata)],
            [new(FileFactKey.VideoKind, "web_clip", FactSource.Model, ResultCertainty.Likely), new(FileFactKey.VideoKindDisputed, "own_recording", FactSource.Name, ResultCertainty.Uncertain)],
            [new(FileFactKey.Width, "1920")]
        ])!;
        Assert.Equal(5, mix.Videos);
        Assert.Equal(["series_episode", "own_recording", "web_clip"], mix.Kinds.Keys);
        Assert.Equal(1, mix.Devices["drone"]);
        Assert.Equal(["Example Show S01-S02"], mix.Works);
        Assert.Equal((4, 1), (mix.Certain, mix.Disputed));
        Assert.Equal("series_episode", mix.Dominant);
        Assert.Equal("5 video(s): 3 series_episode, 1 own_recording, 1 web_clip (drone 1); Example Show S01-S02; 1 disputed by the model", mix.Gist());
        Assert.Null(VideoOriginMix.From([[new(FileFactKey.Width, "1920")]]));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private FileNode Node(string path) => new()
    {
        Id = 1, JobId = 1, FullPath = path, Name = Path.GetFileName(path), Extension = Path.GetExtension(path),
        FileType = FileType.Video, ParentPath = _dir, RootPath = _dir, RelativePath = Path.GetFileName(path), RelativeDir = "",
        Size = new FileInfo(path).Length, LastWriteTime = DateTime.UtcNow
    };
}
