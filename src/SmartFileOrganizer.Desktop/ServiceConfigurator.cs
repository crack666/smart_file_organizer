using Dapper;
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

        // Logging
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

        // Database
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SmartFileOrganizer", "data.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        services.AddSingleton(sp =>
            new DatabaseContext(dbPath, sp.GetRequiredService<ILogger<DatabaseContext>>()));

        // Repositories
        services.AddSingleton<IScanJobRepository, ScanJobRepository>();
        services.AddSingleton<IFileRepository, FileRepository>();
        services.AddSingleton<IDirectoryRepository, DirectoryRepository>();
        services.AddSingleton<IClassificationRepository, ClassificationRepository>();
        services.AddSingleton<IUserOverrideRepository, UserOverrideRepository>();

        // Infrastructure
        services.AddSingleton<IFileSystemAccessor, FileSystemAccessor>();

        // Ollama
        services.AddSingleton<OllamaOptions>();
        services.AddHttpClient<IOllamaService, OllamaService>((sp, client) =>
        {
            var opts = sp.GetRequiredService<OllamaOptions>();
            client.BaseAddress = new Uri(opts.BaseUrl);
        });

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
        services.AddSingleton<ReviewService>();
        services.AddSingleton<FilePreviewService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<FolderTreeViewModel>();
        services.AddTransient<FileTableViewModel>();
        services.AddTransient<FileDetailViewModel>();
        services.AddTransient<ScanProgressViewModel>();

        var provider = services.BuildServiceProvider();

        // Ensure DB is initialized synchronously before the window opens
        provider.GetRequiredService<DatabaseContext>()
            .InitializeAsync().GetAwaiter().GetResult();

        return provider;
    }
}
