using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(SmartFileOrganizer.Desktop.Tests.HeadlessApp))]

namespace SmartFileOrganizer.Desktop.Tests;

/// <summary>
/// The look of the real app (dark Fluent theme with the DataGrid theme) without its start-up, which would open the
/// database of <c>SFO_DB</c> or the default one. Rendered with Skia and no screen, so frames can be checked and saved on
/// Windows and Linux alike; Inter is the default font, so text renders where no system font is installed.
/// </summary>
public sealed class HeadlessApp : Avalonia.Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://SmartFileOrganizer.Desktop.Tests/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        });
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont()
        .With(new FontManagerOptions { DefaultFamilyName = "avares://Avalonia.Fonts.Inter/Assets#Inter" });
}
