using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Application.Library;
using SmartFileOrganizer.Application.Services;
using SmartFileOrganizer.Domain.Interfaces;
using SmartFileOrganizer.Infrastructure;
using SmartFileOrganizer.Infrastructure.Content;
using SmartFileOrganizer.Infrastructure.Ollama;
using SmartFileOrganizer.Infrastructure.Persistence;
using SmartFileOrganizer.Scanning.Engine;

namespace SmartFileOrganizer.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything except the user interface: configuration, SQLite repositories,
    /// scanning, Ollama client, evidence extraction and all application services.
    /// </summary>
    /// <remarks>
    /// Callers add their own logging providers (the core only registers the logging
    /// infrastructure) and must call <see cref="InitializeSmartFileOrganizerDatabaseAsync"/>
    /// on the built provider before using any repository.
    /// To substitute a service (for example a fake <see cref="IOllamaService"/> in tests),
    /// register the replacement after this call.
    /// </remarks>
    public static IServiceCollection AddSmartFileOrganizerCore(
        this IServiceCollection services,
        Action<SmartFileOrganizerHostOptions>? configure = null)
    {
        var options = new SmartFileOrganizerHostOptions();
        configure?.Invoke(options);
        var paths = SmartFileOrganizerPaths.Resolve(options);

        services.AddSingleton(paths);
        services.AddLogging();

        // Configuration: shipped defaults, then the user's overrides
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsPath)!);
        var configuration = new ConfigurationBuilder()
            .SetBasePath(options.AppSettingsDirectory ?? AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: options.ReloadSettingsOnChange)
            .AddJsonFile(paths.SettingsPath, optional: true, reloadOnChange: options.ReloadSettingsOnChange)
            .Build();
        services.TryAddSingleton<IConfiguration>(configuration);

        // Database
        services.AddSingleton(sp =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);
            return new DatabaseContext(paths.DatabasePath, sp.GetRequiredService<ILogger<DatabaseContext>>());
        });

        // Repositories
        services.AddSingleton<IScanJobRepository, ScanJobRepository>();
        services.AddSingleton<IFileRepository, FileRepository>();
        services.AddSingleton<IDirectoryRepository, DirectoryRepository>();
        services.AddSingleton<IClassificationRepository, ClassificationRepository>();
        services.AddSingleton<IFileFactRepository, FileFactRepository>();
        services.AddSingleton<IUserOverrideRepository, UserOverrideRepository>();
        services.AddSingleton<IDirectoryClassificationRepository, DirectoryClassificationRepository>();
        services.AddSingleton<IJobStatisticsRepository, JobStatisticsRepository>();
        services.AddSingleton<IDirectoryVerdictRepository, DirectoryVerdictRepository>();
        services.AddSingleton<IJobReadRepository, JobReadRepository>();
        services.AddSingleton<IContentComparisonRepository, ContentComparisonRepository>();
        services.AddSingleton<IDirectorySampleRepository, DirectorySampleRepository>();
        services.AddSingleton<ILibraryRepository, LibraryRepository>();

        // Infrastructure
        services.AddSingleton<IFileSystemAccessor, FileSystemAccessor>();
        services.AddSingleton<FileContentExtractor>();
        services.AddSingleton(_ => new VideoFrameExtractor());
        services.AddSingleton<IClassificationInputPreparer>(sp => new AiClassificationInputPreparer(
            sp.GetRequiredService<FileContentExtractor>(),
            sp.GetRequiredService<OllamaOptions>(),
            sp.GetRequiredService<VideoFrameExtractor>()));
        services.AddSingleton<IContentHasher, ContentHasher>();

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
        services.AddHttpClient<ILibraryAdvisor, OllamaLibraryAdvisor>((sp, client) =>
        {
            var opts = sp.GetRequiredService<OllamaOptions>();
            client.BaseAddress = new Uri(opts.BaseUrl);
            // The advisor applies its own, longer timeout per call.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton(sp => new OllamaSettingsService(
            sp.GetRequiredService<OllamaOptions>(),
            paths.SettingsPath));

        // Scanning
        services.AddSingleton<IScanEngine, ScanEngine>();
        services.AddSingleton<IScopedRefreshService, ScopedRefreshService>();

        // Application services
        services.AddSingleton<ScanJobService>();
        services.AddSingleton<JobStatusService>();
        services.AddSingleton<FileQueryService>();
        services.AddSingleton<DirectoryQueryService>();
        services.AddSingleton<JobReadService>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ClassificationService>(sp => new ClassificationService(
            sp.GetRequiredService<IFileRepository>(),
            sp.GetRequiredService<IClassificationRepository>(),
            sp.GetRequiredService<IFileFactRepository>(),
            sp.GetRequiredService<IClassificationInputPreparer>(),
            sp.GetRequiredService<IOllamaService>(),
            sp.GetRequiredService<DirectorySamplingService>(),
            () => sp.GetRequiredService<OllamaOptions>().MaxParallelRequests,
            sp.GetRequiredService<ILogger<ClassificationService>>()));
        services.AddSingleton(sp =>
        {
            // Section "Sampling"; the sample size used to live under "Ollama" and is still read from there.
            var section = sp.GetRequiredService<IConfiguration>().GetSection("Sampling");
            var analysis = section.Get<DirectoryAnalysisOptions>() ?? new DirectoryAnalysisOptions();
            if (section["MaxSamplesPerDirectory"] is null)
                analysis.MaxSamplesPerDirectory = sp.GetRequiredService<OllamaOptions>().MaxSamplesPerDirectory;
            return analysis;
        });
        services.AddSingleton<DirectorySamplingService>();
        services.AddSingleton<DirectoryPreAssessmentService>(sp => new DirectoryPreAssessmentService(
            sp.GetRequiredService<IDirectoryRepository>(),
            sp.GetRequiredService<IFileRepository>(),
            sp.GetRequiredService<IDirectoryClassificationRepository>(),
            sp.GetRequiredService<IDirectoryVerdictRepository>(),
            sp.GetRequiredService<IOllamaService>(),
            () => sp.GetRequiredService<OllamaOptions>().MaxParallelRequests,
            sp.GetRequiredService<DirectoryAnalysisOptions>(),
            sp.GetRequiredService<ILogger<DirectoryPreAssessmentService>>()));
        services.AddSingleton<DirectorySummaryService>(sp => new DirectorySummaryService(
            sp.GetRequiredService<IDirectoryRepository>(),
            sp.GetRequiredService<IFileRepository>(),
            sp.GetRequiredService<IDirectoryClassificationRepository>(),
            sp.GetRequiredService<IClassificationRepository>(),
            sp.GetRequiredService<IDirectoryVerdictRepository>(),
            sp.GetRequiredService<IDirectorySampleRepository>(),
            sp.GetRequiredService<IOllamaService>(),
            () => sp.GetRequiredService<OllamaOptions>().MaxParallelRequests,
            sp.GetRequiredService<ILogger<DirectorySummaryService>>()));
        services.AddSingleton<AiClassificationCoordinator>();
        services.AddSingleton<ReviewService>();
        services.AddSingleton<ScanExportService>();
        services.AddSingleton<FocusedRescanService>();
        services.AddSingleton<ContentComparisonService>();

        return services;
    }

    /// <summary>Creates the database if needed and applies pending schema steps. Returns the schema version.</summary>
    public static Task<int> InitializeSmartFileOrganizerDatabaseAsync(
        this IServiceProvider services, CancellationToken ct = default)
    {
        return services.GetRequiredService<DatabaseContext>().InitializeAsync(ct);
    }
}
