using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Desktop.Services;
using SmartFileOrganizer.Desktop.ViewModels;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Hosting;
using SmartFileOrganizer.Infrastructure.Ollama;

namespace SmartFileOrganizer.Desktop;

/// <summary>
/// GUI composition root. Everything except views, view models and GUI-only services comes
/// from <see cref="ServiceCollectionExtensions.AddSmartFileOrganizerCore"/>.
/// </summary>
public static class ServiceConfigurator
{
    public static IServiceProvider Build()
    {
        var services = new ServiceCollection();

        // Logging — AddDebug() writes to the VS "Output > Debug" channel (works with WinExe)
        services.AddLogging(b => b.AddConsole().AddDebug().SetMinimumLevel(LogLevel.Debug));

        // Core: database (SFO_DB or %APPDATA%), settings, repositories, scanning, Ollama, app services
        services.AddSmartFileOrganizerCore();

        // GUI-only services
        services.AddSingleton<FilePreviewService>();

        // ViewModels
        services.AddTransient<MainViewModel>(sp => new MainViewModel(
            sp.GetRequiredService<ScanJobService>(),
            sp.GetRequiredService<FileQueryService>(),
            sp.GetRequiredService<AiClassificationCoordinator>(),
            sp.GetRequiredService<IOllamaService>(),
            sp.GetRequiredService<OllamaOptions>(),
            sp.GetRequiredService<OllamaSettingsService>(),
            sp.GetRequiredService<FolderTreeViewModel>(),
            sp.GetRequiredService<FileTableViewModel>(),
            sp.GetRequiredService<FileDetailViewModel>(),
            sp.GetRequiredService<ScanProgressViewModel>(),
            sp.GetRequiredService<AiProgressViewModel>(),
            sp.GetRequiredService<IDirectoryClassificationRepository>(),
            sp.GetRequiredService<FocusedRescanService>()));
        services.AddTransient<FolderTreeViewModel>();
        services.AddTransient<FileTableViewModel>(sp => new FileTableViewModel(
            sp.GetRequiredService<FileQueryService>(),
            sp.GetRequiredService<ReviewService>()));
        services.AddTransient<FileDetailViewModel>(sp => new FileDetailViewModel(
            sp.GetRequiredService<FilePreviewService>(),
            sp.GetRequiredService<ReviewService>()));
        services.AddTransient<ScanProgressViewModel>();
        services.AddTransient<AiProgressViewModel>();

        var provider = services.BuildServiceProvider();

        // Ensure DB is initialized synchronously before the window opens
        provider.InitializeSmartFileOrganizerDatabaseAsync().GetAwaiter().GetResult();

        return provider;
    }
}
