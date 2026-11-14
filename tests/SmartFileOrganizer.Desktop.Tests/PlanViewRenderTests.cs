using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using SmartFileOrganizer.Desktop.ViewModels;
using SmartFileOrganizer.Desktop.Views;
using SmartFileOrganizer.Domain.Enums;

namespace SmartFileOrganizer.Desktop.Tests;

/// <summary>
/// The plan view inside the real main window, rendered without a screen: one pass from the agent's plan to the undo,
/// each stage checked to draw real content (not a blank or black frame). With <c>SFO_SCREENSHOT_DIR</c> set, the
/// frames are saved there as PNG, from a test folder <c>sfo-demo</c> in the temp folder (docs/images holds them).
/// </summary>
public class PlanViewRenderTests
{
    private const string ScreenshotVariable = "SFO_SCREENSHOT_DIR";

    [AvaloniaFact]
    public async Task OnePass_InTheMainWindow_FromTheAgentsPlanToUndo()
    {
        var shots = Environment.GetEnvironmentVariable(ScreenshotVariable) is { Length: > 0 } d ? Directory.CreateDirectory(d).FullName : null;
        using var bed = new PlanTestbed(shots is null ? null : Path.Combine(Path.GetTempPath(), "sfo-demo"));
        await bed.CreatePlanAsync();

        var services = bed.GuiServices();
        var main = services.GetRequiredService<MainViewModel>();
        // Shown in the toolbar only; the model server is a stand-in and is never called.
        main.OllamaBaseUrl = "http://localhost:11434";
        var window = new MainWindow { DataContext = main, Width = 1300, Height = 900 };
        window.Show();
        await main.OpenPlanAsync();
        var plan = main.Plan;
        Assert.True(main.IsShowingPlan);
        Assert.False(main.IsBrowserVisible);
        Assert.Equal(bed.PlanId, plan.SelectedPlan!.Plan.Id);

        // 1: the plan as the agent left it, a photo selected: one question per file and what executing does.
        plan.SelectedRow = plan.AllActions.First(r => r.Action.RelativePath == "Downloads/IMG_20230812_143012.jpg");
        await Until(() => plan.HasImagePreview);
        Assert.Contains(Visible(window), t => t.Contains(bed.DatabasePath, StringComparison.Ordinal));
        Assert.Contains(Visible(window), t => t == "Agent");
        Assert.Contains(Visible(window), t => t == "Was soll damit geschehen?");
        Assert.Contains(Visible(window), t => t.StartsWith("Wird nach bibliothek/Fotos/", StringComparison.Ordinal));
        Frame(window, shots, "01-plan-agent.png");

        // 2: two photos into the bin, the shopping list stays; a binned photo in the detail.
        foreach (var r in plan.AllActions.Where(r => r.Action.RelativePath.StartsWith("Downloads/IMG_", StringComparison.Ordinal)))
            r.IsChecked = true;
        await plan.BinCheckedCommand.ExecuteAsync(null);
        plan.ClearChecksCommand.Execute(null);
        plan.SelectedRow = plan.AllActions.Single(r => r.Action.RelativePath == "Downloads/Einkaufsliste.txt");
        await plan.SetSelectedOutcomeCommand.ExecuteAsync(PlanOutcome.Stay);
        plan.SelectedRow = plan.AllActions.First(r => r.Action.RelativePath == "Downloads/IMG_20230812_143512.jpg");
        await Until(() => plan.HasImagePreview);
        Assert.Contains(Visible(window), t => t == "Papierkorb");
        Assert.Contains(Visible(window), t => t == "Hier lassen");
        Frame(window, shots, "02-outcomes.png");

        // 3: "Ziel ändern" for the invoice: the folder tree of the library, with the inbox and a new folder.
        plan.SelectedRow = plan.AllActions.Single(r => r.Action.RelativePath == "Downloads/Rechnung_Strom.pdf");
        var picking = plan.ChangeTargetSelectedCommand.ExecuteAsync(null);
        await Until(() => plan.TargetPicker is { Roots.Count: 1 } p && p.Roots[0].Children.Any(c => c.Label == "Fotos"));
        var picker = plan.TargetPicker!;
        var fotos = picker.Roots[0].Children.Single(c => c.Label == "Fotos");
        fotos.IsExpanded = true;
        await Until(() => fotos.Children.Any(c => c.Label == "2019-06 Italien"));
        picker.SelectedFolder = picker.Roots[0];
        picker.NewFolderName = "Rechnungen";
        picker.AddFolderCommand.Execute(null);
        await Until(() => Visible(window).Any(t => t.StartsWith("Eingangsordner", StringComparison.Ordinal)));
        await Until(() => Visible(window).Any(t => t.StartsWith("Neues Ziel: bibliothek/Rechnungen/Rechnung_Strom.pdf", StringComparison.Ordinal)));
        Frame(window, shots, "03-target.png");
        await picker.AcceptAsync();
        await picking;

        // 4: the person sets back what the agent decided and releases everything at once.
        await plan.ResetAgentDecisionsCommand.ExecuteAsync(null);
        await plan.ReleaseAllCommand.ExecuteAsync(null);
        Assert.DoesNotContain(plan.AllActions, r => r.DecidedByAgent);
        Assert.Contains(Visible(window), t => t.StartsWith("Freigegeben: ", StringComparison.Ordinal));
        Frame(window, shots, "04-released.png");

        // 5: before step 1, the summary in the words of the outcomes.
        var step1 = plan.ApplyCommand.ExecuteAsync(null);
        await Until(() => plan.Confirmation is not null);
        Assert.True(plan.Confirmation!.Lines.Any(l => l.StartsWith("In den Papierkorb: 2 Aktionen", StringComparison.Ordinal)),
            string.Join(" | ", plan.Confirmation.Lines));
        await Until(() => Visible(window).Any(t => t.StartsWith("In den Papierkorb: 2 Aktionen", StringComparison.Ordinal)));
        Frame(window, shots, "05-summary-step1.png");
        plan.Confirmation!.AcceptCommand.Execute(null);
        await step1;
        Assert.Contains(plan.AllActions, r => r.Action.State == PlanActionState.Verified);
        var step2 = plan.RemoveSourcesCommand.ExecuteAsync(null);
        await Until(() => plan.Confirmation is not null);
        Assert.Contains(Visible(window), t => t == "In den Papierkorb");
        Frame(window, shots, "06-confirm-step2.png");
        plan.Confirmation!.AcceptCommand.Execute(null);
        await step2;
        Assert.Contains(plan.AllActions, r => r.Action.State == PlanActionState.Executed);
        await Until(() => plan.HasBinHeld);
        Assert.Contains(Visible(window), t => t.StartsWith("Im Papierkorb: ", StringComparison.Ordinal));
        Frame(window, shots, "07-executed.png");

        // 6: "Papierkorb leeren" asks first with count and size (#105); declined here, so the undo below still works.
        var empty = plan.EmptyBinCommand.ExecuteAsync(null);
        await Until(() => plan.Confirmation is not null);
        await Until(() => Visible(window).Any(t => t.StartsWith("Endgültig gelöscht werden ", StringComparison.Ordinal)));
        Frame(window, shots, "09-confirm-empty-bin.png");
        plan.Confirmation!.CancelCommand.Execute(null);
        await empty;
        Assert.True(plan.HasBinHeld);

        // 6: taken back.
        var undo = plan.UndoCommand.ExecuteAsync(null);
        await Until(() => plan.Confirmation is not null);
        plan.Confirmation!.AcceptCommand.Execute(null);
        await undo;
        Assert.Contains(plan.AllActions, r => r.Action.State == PlanActionState.Undone);
        Frame(window, shots, "08-undone.png");
        window.Close();
    }

    /// <summary>Renders the window, checks the frame has real content, and saves it when screenshots are asked for.</summary>
    private static void Frame(Window window, string? dir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame rendered");
        using var png = new MemoryStream();
        frame.Save(png);
        png.Position = 0;
        using var bitmap = SKBitmap.Decode(png);
        Assert.True(bitmap.Width >= 1200 && bitmap.Height >= 700, $"{name}: {bitmap.Width}x{bitmap.Height}");

        // A blank or black frame has a handful of colours; text, badges and a photo give hundreds.
        var colours = new HashSet<uint>();
        var bright = 0;
        for (var y = 0; y < bitmap.Height; y += 3)
        for (var x = 0; x < bitmap.Width; x += 3)
        {
            var c = bitmap.GetPixel(x, y);
            colours.Add((uint)c);
            if (c.Red + c.Green + c.Blue > 400) bright++;
        }
        Assert.True(colours.Count > 200, $"{name}: only {colours.Count} colours");
        Assert.True(bright > 500, $"{name}: almost no light pixels ({bright})");

        if (dir is not null)
            File.WriteAllBytes(Path.Combine(dir, name), png.ToArray());
    }

    private static List<string> Visible(Window window) => window.GetVisualDescendants()
        .OfType<TextBlock>()
        .Where(t => t.IsEffectivelyVisible)
        .Select(t => t.Text ?? string.Join("", t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? []))
        .ToList();

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition(), "condition not met in time");
    }
}
