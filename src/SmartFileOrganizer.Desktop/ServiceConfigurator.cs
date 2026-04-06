using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Desktop.Services;
using SmartFileOrganizer.Desktop.ViewModels;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure;
using SmartFileOrganizer.Infrastructure.Ollama;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Scanning.Engine;

namespace SmartFileOrganizer.Desktop;

public static class ServiceConfigurator
{
    public static IServiceProvider Build()
    {
        // Dapper: map snake_case column names to PascalCase C# properties
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        var services = new ServiceCollection();

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SmartFileOrganizer");
        Directory.CreateDirectory(appDataDir);
        var settingsPath = Path.Combine(appDataDir, "settings.json");

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile(settingsPath, optional: true, reloadOnChange: true)
            .Build();

        services.AddSingleton<IConfiguration>(configuration);

        // Logging — AddDebug() writes to the VS "Output > Debug" channel (works with WinExe)
        services.AddLogging(b => b.AddConsole().AddDebug().SetMinimumLevel(LogLevel.Debug));

        // Database
        var dbPath = Path.Combine(appDataDir, "data.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        services.AddSingleton(sp =>
            new DatabaseContext(dbPath, sp.GetRequiredService<ILogger<DatabaseContext>>()));

        // Repositories
        services.AddSingleton<IScanJobRepository, ScanJobRepository>();
        services.AddSingleton<IFileRepository, FileRepository>();
        services.AddSingleton<IDirectoryRepository, DirectoryRepository>();
        services.AddSingleton<IClassificationRepository, ClassificationRepository>();
        services.AddSingleton<IUserOverrideRepository, UserOverrideRepository>();
        services.AddSingleton<IDirectoryClassificationRepository, DirectoryClassificationRepository>();

        // Infrastructure
        services.AddSingleton<IFileSystemAccessor, FileSystemAccessor>();

        // Ollama
        services.AddSingleton(sp =>
        {
            var configured = sp.GetRequiredService<IConfiguration>()
                .GetSection("Ollama")
                .Get<OllamaOptions>();

            return configured ?? new OllamaOptions();
        });
        services.AddHttpClient<IOllamaService, OllamaService>((sp, client) =>
        {
            var opts = sp.GetRequiredService<OllamaOptions>();
            client.BaseAddress = new Uri(opts.BaseUrl);
        });
        services.AddSingleton(sp => new OllamaSettingsService(
            sp.GetRequiredService<OllamaOptions>(),
            settingsPath));

        // Scanning
        services.AddSingleton<HeuristicsOptions>();
        services.AddSingleton<IHeuristicsEngine>(sp =>
            new HeuristicsEngine(sp.GetRequiredService<HeuristicsOptions>()));
        services.AddSingleton<IScanEngine, ScanEngine>();

        // Application services
        services.AddSingleton<ScanJobService>();
        services.AddSingleton<FileQueryService>();
        services.AddSingleton<DirectoryQueryService>();
        services.AddSingleton<ClassificationService>();
        services.AddSingleton(sp => new DirectoryAnalysisOptions
        {
            MaxSamplesPerDirectory = sp.GetRequiredService<OllamaOptions>().MaxSamplesPerDirectory
        });
        services.AddSingleton<DirectoryPreAssessmentService>();
        services.AddSingleton<DirectorySummaryService>();
        services.AddSingleton<AiClassificationCoordinator>();
        services.AddSingleton<ReviewService>();
        services.AddSingleton<FilePreviewService>();
        services.AddSingleton<IClassificationInputPreparer, AiClassificationInputPreparer>();

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
            sp.GetRequiredService<IDirectoryClassificationRepository>()));
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
        provider.GetRequiredService<DatabaseContext>()
            .InitializeAsync().GetAwaiter().GetResult();

        return provider;
    }
}
