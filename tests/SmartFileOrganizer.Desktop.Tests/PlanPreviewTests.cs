using Avalonia.Headless.XUnit;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Desktop.ViewModels;
using SmartFileOrganizer.Infrastructure.Content;

namespace SmartFileOrganizer.Desktop.Tests;

/// <summary>
/// The preview of the plan view for videos and formats that had none, and what "Alles freigeben" says
/// (https://github.com/crack666/smart_file_organizer/issues/109). The shell is a stand-in: "Abspielen" never starts a
/// program. The video is made by ffmpeg when it is on the PATH; the case without it is forced through the testbed.
/// </summary>
public class PlanPreviewTests
{
    [AvaloniaFact]
    public async Task Video_GetsAFilmstripInTheBackground_KeptInTheAppCache_AndPlayHandsTheFileToTheShell()
    {
        using var bed = new PlanTestbed(withMedia: true);
        await bed.CreatePlanAsync();
        var vm = await bed.OpenPlanAsync();
        var video = Row(vm, "Ausflug_2023.mp4");
        var tree = Files(bed.Tree);
        var library = Files(bed.Library);

        vm.SelectedRow = video;
        await WaitUntil(() => vm.CanPlay);
        if (bed.HasRealVideo)
        {
            await WaitUntil(() => vm.HasFrames);
            Assert.Equal(FilePreviewServiceFrames, vm.PreviewFrames.Count);
            Assert.Equal(["0:00", "0:02", "0:03", "0:04", "0:06", "0:07", "0:08", "0:10", "0:11"], vm.PreviewFrames.Select(f => f.Label));
            Assert.All(vm.PreviewFrames, f => Assert.Equal(Services.FilePreviewService.FilmstripWidth, f.Image.PixelSize.Width));
            Assert.StartsWith("9 Bilder", vm.PreviewMessage);
            // Kept in the cache of the application, not beside the video and not in the library.
            Assert.Equal(9, Directory.GetFiles(bed.PreviewCache, "*.png", SearchOption.AllDirectories).Length);
            Assert.Equal(tree, Files(bed.Tree));
            Assert.Equal(library, Files(bed.Library));

            // A second look comes from the cache: a tool that cannot run still gives the strip.
            var broken = new Services.FilePreviewService(new FileContentExtractor(),
                new VideoFrameExtractor(() => Path.Combine(bed.Root, "no-such-tool.exe")), bed.PreviewCache);
            var again = await broken.BuildAsync((await FileOf(bed, video))!);
            Assert.Equal(9, again.Frames.Count);
            again.Dispose();
        }
        else
        {
            await WaitUntil(() => vm.PreviewMessage.Contains("konnte das Video nicht lesen", StringComparison.Ordinal));
        }

        vm.PlaySelectedCommand.Execute(null);
        Assert.Equal([video.Action.SourcePath], bed.Shell.Opened);
    }

    [AvaloniaFact]
    public async Task Video_WithoutFfmpeg_SaysSo_AndPlayIsTheWay()
    {
        using var bed = new PlanTestbed(withMedia: true) { WithoutFfmpeg = true };
        await bed.CreatePlanAsync();
        var vm = await bed.OpenPlanAsync();
        var video = Row(vm, "Ausflug_2023.mp4");

        vm.SelectedRow = video;
        await WaitUntil(() => vm.HasPreviewMessage && vm.CanPlay);
        Assert.StartsWith("Kein Bildstreifen: ffmpeg ist auf diesem Rechner nicht gefunden.", vm.PreviewMessage);
        Assert.Contains("„Abspielen“", vm.PreviewMessage);
        Assert.False(vm.HasFrames);
        Assert.False(Directory.Exists(bed.PreviewCache));

        vm.PlaySelectedCommand.Execute(null);
        Assert.Equal([video.Action.SourcePath], bed.Shell.Opened);
    }

    [AvaloniaFact]
    public async Task Jfif_ShowsAPicture_Doc_ShowsItsText_AndAnUnknownFormat_SaysWhy()
    {
        using var bed = new PlanTestbed(withMedia: true);
        await bed.CreatePlanAsync();
        var vm = await bed.OpenPlanAsync();

        vm.SelectedRow = Row(vm, "Postkarte.jfif");
        await WaitUntil(() => vm.HasImagePreview);
        Assert.False(vm.CanPlay);

        vm.SelectedRow = Row(vm, "Mietvertrag_alt.doc");
        await WaitUntil(() => vm.HasTextPreview);
        Assert.StartsWith("Mietvertrag\nzwischen Vermieterin Muster", vm.TextPreview);
        Assert.Contains("Kaution: 900 € – fällig zum Einzug", vm.TextPreview);
        Assert.Contains("Übergabe der Schlüssel", vm.TextPreview);
        Assert.False(vm.HasImagePreview);

        vm.SelectedRow = Row(vm, "Tabelle_alt.xls");
        await WaitUntil(() => vm.HasPreviewMessage);
        Assert.StartsWith("Kein Textauszug: altes Office-Format (XLS)", vm.PreviewMessage);
        Assert.False(vm.HasImagePreview || vm.HasTextPreview || vm.HasFrames);

        // A format with no reader at all (the scan gives such files no action in this plan, so directly).
        var unknown = Path.Combine(bed.Root, "messwerte.xyz");
        File.WriteAllBytes(unknown, [0x00, 0x53, 0x46, 0x4F, 0x00, 0xFF, 0x10, 0x20, 0x00, 0x01]);
        var preview = await new Services.FilePreviewService(new FileContentExtractor(), null, bed.PreviewCache).BuildAsync(
            new Domain.Models.FileNode { Name = "messwerte.xyz", FullPath = unknown, Extension = ".xyz", FileType = Domain.Enums.FileType.Other });
        Assert.StartsWith("Keine Vorschau für XYZ", preview.Message);
        Assert.False(preview.HasImagePreview || preview.HasTextPreview || preview.Frames.Count > 0);
    }

    [AvaloniaFact]
    public async Task ChangingTheRow_DropsThePreviewOfTheOldOne()
    {
        using var bed = new PlanTestbed(withMedia: true);
        await bed.CreatePlanAsync();
        var vm = await bed.OpenPlanAsync();

        // The video is chosen and left at once: its strip must not land on the text document chosen next.
        vm.SelectedRow = Row(vm, "Ausflug_2023.mp4");
        vm.SelectedRow = Row(vm, "Einkaufsliste.txt");
        await WaitUntil(() => vm.HasTextPreview);
        await Task.Delay(bed.HasRealVideo ? 1500 : 100);
        Assert.False(vm.HasFrames);
        Assert.False(vm.CanPlay);
        Assert.StartsWith("Einkaufsliste", vm.TextPreview);
    }

    [AvaloniaFact]
    public async Task ReleaseAll_AsksFirst_NamesWhatIsSet_AndChangesNoOutcome()
    {
        using var bed = new PlanTestbed();
        await bed.CreatePlanAsync(agentApproves: false);
        var vm = await bed.OpenPlanAsync();
        // One photo into the bin, the shopping list stays: three outcomes in the plan.
        vm.SelectedRow = Row(vm, "IMG_20230812_143012.jpg");
        await vm.SetSelectedOutcomeCommand.ExecuteAsync(PlanOutcome.Bin);
        vm.SelectedRow = Row(vm, "Einkaufsliste.txt");
        await vm.SetSelectedOutcomeCommand.ExecuteAsync(PlanOutcome.Stay);
        var before = vm.AllActions.ToDictionary(r => r.Id, r => (r.Action.Kind, r.Action.TargetPath, r.Outcome));
        var move = vm.AllActions.Count(r => r.Outcome == PlanOutcome.Move && !r.Action.SingleDecision);
        var stay = vm.AllActions.Count(r => r.Outcome == PlanOutcome.Stay && !r.Action.SingleDecision);

        // Declined: nothing released.
        var declined = vm.ReleaseAllCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Confirmation is not null);
        Assert.Equal("Alles freigeben?", vm.Confirmation!.Title);
        Assert.Equal($"Freigegeben wird, was eingestellt ist: {move} ziehen um, 1 kommen in den Papierkorb, {stay} bleiben hier.", vm.Confirmation.Lines[0]);
        Assert.StartsWith("Kein Ausgang wird geändert", vm.Confirmation.Lines[1]);
        Assert.Contains(vm.Confirmation.Lines, l => l.StartsWith("Nicht dabei: 1 sensible", StringComparison.Ordinal));
        vm.Confirmation.CancelCommand.Execute(null);
        await declined;
        Assert.DoesNotContain(vm.AllActions, r => r.IsReleased);

        // Accepted: released as set, every kind, target and outcome as before.
        var accepted = vm.ReleaseAllCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Confirmation is not null);
        vm.Confirmation!.AcceptCommand.Execute(null);
        await accepted;
        Assert.All(vm.AllActions.Where(r => !r.Action.SingleDecision), r => Assert.True(r.IsReleased));
        Assert.Equal(before, vm.AllActions.ToDictionary(r => r.Id, r => (r.Action.Kind, r.Action.TargetPath, r.Outcome)));
        Assert.Contains("Kein Ausgang wird geändert", PlanViewModel.ReleaseAllHint);
    }

    [AvaloniaFact]
    public async Task VideoOrigin_IsOneLineInWords_FromTheFacts()
    {
        var file = new Domain.Models.FileNode { Name = "clip.mp4", FileType = Domain.Enums.FileType.Video };
        Assert.Equal("Art: Eigene Aufnahme · Handy · aus den Metadaten, sicher", PlanViewModel.OriginLine(file,
            [new(Domain.Enums.FileFactKey.VideoKind, "own_recording", Domain.Enums.FactSource.Metadata, Domain.Enums.ResultCertainty.Certain),
             new(Domain.Enums.FileFactKey.RecordingDevice, "phone", Domain.Enums.FactSource.Metadata)], showsFreeText: true));
        Assert.Equal("Art: Serienfolge · Staffel 2, Folge 5 · aus dem Namen, sicher", PlanViewModel.OriginLine(file,
            [new(Domain.Enums.FileFactKey.VideoKind, "series_episode", Domain.Enums.FactSource.Name, Domain.Enums.ResultCertainty.Certain),
             new(Domain.Enums.FileFactKey.SeasonNumber, "2", Domain.Enums.FactSource.Name), new(Domain.Enums.FileFactKey.EpisodeNumber, "5", Domain.Enums.FactSource.Name),
             new(Domain.Enums.FileFactKey.VideoTitle, "Secret Show", Domain.Enums.FactSource.Name)], showsFreeText: false));
        Assert.Equal(string.Empty, PlanViewModel.OriginLine(file, [], true));
        await Task.CompletedTask;
    }

    private const int FilePreviewServiceFrames = Services.FilePreviewService.FilmstripFrames;

    private static PlanActionRowViewModel Row(PlanViewModel vm, string name) =>
        vm.AllActions.SingleOrDefault(r => Path.GetFileName(r.Action.SourcePath) == name)
        ?? throw new InvalidOperationException($"No action for {name}: " + string.Join(", ", vm.AllActions.Select(r => Path.GetFileName(r.Action.SourcePath))));

    private static async Task<Domain.Models.FileNode?> FileOf(PlanTestbed bed, PlanActionRowViewModel row)
    {
        var files = (Application.Services.FileQueryService)bed.GuiServices().GetService(typeof(Application.Services.FileQueryService))!;
        return await files.GetFileByIdAsync(row.Action.FileNodeId!.Value);
    }

    private static SortedSet<string> Files(string root) =>
        new(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)), StringComparer.Ordinal);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition not met in time");
    }
}
