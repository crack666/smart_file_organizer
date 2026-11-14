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

        // 1: the plan as the agent left it, a photo selected.
        plan.SelectedRow = plan.AllActions.First(r => r.Action.RelativePath == "Downloads/IMG_20230812_143012.jpg");
        await Until(() => plan.HasImagePreview);
        Assert.Contains(Visible(window), t => t.Contains(bed.DatabasePath, StringComparison.Ordinal));
        Assert.Contains(Visible(window), t => t == "Agent");
        Frame(window, shots, "01-plan-agent.png");

        // 2: two photos marked as clutter, the text file in the detail.
        foreach (var r in plan.AllActions.Where(r => r.Action.RelativePath.StartsWith("Downloads/IMG_", StringComparison.Ordinal)))
            r.IsChecked = true;
        await plan.MarkCheckedForRemovalCommand.ExecuteAsync(null);
        plan.SelectedRow = plan.AllActions.Single(r => r.Action.RelativePath == "Downloads/Einkaufsliste.txt");
        await Until(() => plan.HasTextPreview);
        Assert.Contains(Visible(window), t => t == "Quarantäne");
        Frame(window, shots, "02-clutter-and-text.png");

        // 3: the person confirms the clutter and the rest by hand, sets back what the agent decided, dry run.
        await plan.ResetAgentDecisionsCommand.ExecuteAsync(null);
        await plan.ApproveAllCommand.ExecuteAsync(null);
        Assert.DoesNotContain(plan.AllActions, r => r.DecidedByAgent);
        await plan.DryRunCommand.ExecuteAsync(null);
        Assert.Contains(Visible(window), t => t.Contains("würden laufen", StringComparison.Ordinal));
        Frame(window, shots, "03-dry-run.png");

        // 4: step 1 executed, then the confirmation of step 2 with what goes.
        var step1 = plan.ApplyCommand.ExecuteAsync(null);
        await Until(() => plan.Confirmation is not null);
        Frame(window, shots, "04-confirm-step1.png");
        plan.Confirmation!.AcceptCommand.Execute(null);
        await step1;
        Assert.Contains(plan.AllActions, r => r.Action.State == PlanActionState.Verified);
        var step2 = plan.RemoveSourcesCommand.ExecuteAsync(null);
        await Until(() => plan.Confirmation is not null);
        Assert.Contains(Visible(window), t => t == "Originale entfernen");
        Frame(window, shots, "05-confirm-step2.png");
        plan.Confirmation!.AcceptCommand.Execute(null);
        await step2;
        Assert.Contains(plan.AllActions, r => r.Action.State == PlanActionState.Executed);
        Frame(window, shots, "06-executed.png");

        // 5: taken back.
        var undo = plan.UndoCommand.ExecuteAsync(null);
        await Until(() => plan.Confirmation is not null);
        plan.Confirmation!.AcceptCommand.Execute(null);
        await undo;
        Assert.Contains(plan.AllActions, r => r.Action.State == PlanActionState.Undone);
        Frame(window, shots, "07-undone.png");
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
