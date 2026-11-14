using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Application.Plans;
using SmartFileOrganizer.Application.Sensitivity;
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
    public static IServiceProvider Build() => Build(configure: null, overrides: null);

    /// <summary>
    /// The GUI's services. <paramref name="configure"/> sets the host options; without it the database comes from
    /// <c>SFO_DB</c> and the settings from <c>SFO_SETTINGS</c> (else next to an overridden database, else the defaults),
    /// as in the CLI. <paramref name="overrides"/> replaces services after the core registered them (tests: a stand-in
    /// model server, the protected places of a temp folder).
    /// </summary>
    public static IServiceProvider Build(Action<SmartFileOrganizerHostOptions>? configure, Action<IServiceCollection>? overrides)
    {
        var services = new ServiceCollection();

        // Logging — AddDebug() writes to the VS "Output > Debug" channel (works with WinExe)
        services.AddLogging(b => b.AddConsole().AddDebug().SetMinimumLevel(LogLevel.Debug));

        // Core: database (option, else SFO_DB, else %APPDATA%), settings, repositories, scanning, Ollama, app services
        services.AddSmartFileOrganizerCore(configure);

        // GUI-only services
        services.AddSingleton<FilePreviewService>();
        services.AddSingleton<IShellLauncher, ShellLauncher>();

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
            sp.GetRequiredService<FocusedRescanService>(),
            sp.GetRequiredService<PlanViewModel>()));
        services.AddTransient<FolderTreeViewModel>();
        services.AddTransient<FileTableViewModel>(sp => new FileTableViewModel(
            sp.GetRequiredService<FileQueryService>(),
            sp.GetRequiredService<ReviewService>()));
        services.AddTransient<FileDetailViewModel>(sp => new FileDetailViewModel(
            sp.GetRequiredService<FilePreviewService>(),
            sp.GetRequiredService<ReviewService>()));
        services.AddTransient<ScanProgressViewModel>();
        services.AddTransient<AiProgressViewModel>();
        // The plan of a job (https://github.com/crack666/smart_file_organizer/issues/15): only the plan services, and the
        // folder view of the libraries for "Ziel ändern" (https://github.com/crack666/smart_file_organizer/issues/102).
        services.AddTransient<PlanViewModel>(sp => new PlanViewModel(
            sp.GetRequiredService<PlanService>(),
            sp.GetRequiredService<PlanExecutor>(),
            sp.GetRequiredService<LibraryTargetService>(),
            sp.GetRequiredService<ScanJobService>(),
            sp.GetRequiredService<FileQueryService>(),
            sp.GetRequiredService<IFileFactRepository>(),
            sp.GetRequiredService<FilePreviewService>(),
            sp.GetRequiredService<IShellLauncher>(),
            sp.GetRequiredService<SensitiveOutputOptions>(),
            sp.GetRequiredService<SmartFileOrganizerPaths>()));

        overrides?.Invoke(services);

        var provider = services.BuildServiceProvider();

        // Ensure DB is initialized synchronously before the window opens
        provider.InitializeSmartFileOrganizerDatabaseAsync().GetAwaiter().GetResult();

        return provider;
    }
}
