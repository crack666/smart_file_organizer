namespace SmartFileOrganizer.Hosting;

/// <summary>Options for <see cref="ServiceCollectionExtensions.AddSmartFileOrganizerCore"/>.</summary>
public sealed class SmartFileOrganizerHostOptions
{
    /// <summary>Environment variable that overrides the database path.</summary>
    public const string DatabasePathVariable = "SFO_DB";

    /// <summary>Environment variable that overrides the user settings file path.</summary>
    public const string SettingsPathVariable = "SFO_SETTINGS";

    /// <summary>
    /// Environment variable with the model server address to use when <c>settings.json</c> names none: it replaces
    /// the shipped default (<c>Ollama:BaseUrl</c> of <c>appsettings.json</c>), never a user's setting. The test projects
    /// set it to a dead address, so no test reaches a real model server by falling back to the default.
    /// </summary>
    public const string OllamaUrlVariable = "SFO_OLLAMA_URL";

    /// <summary>SQLite database file. Takes precedence over <c>SFO_DB</c>.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>User settings file (Ollama overrides). Takes precedence over <c>SFO_SETTINGS</c>.</summary>
    public string? SettingsPath { get; set; }

    /// <summary>Directory containing the shipped <c>appsettings.json</c> defaults.
    /// Defaults to the application base directory.</summary>
    public string? AppSettingsDirectory { get; set; }

    /// <summary>Reload settings when the files change on disk (the GUI relies on this).</summary>
    public bool ReloadSettingsOnChange { get; set; } = true;
}

/// <summary>The file locations a host actually uses, after options, environment and defaults are applied.</summary>
public sealed record SmartFileOrganizerPaths(string DatabasePath, string SettingsPath)
{
    /// <summary><c>%APPDATA%/SmartFileOrganizer</c> (or the platform equivalent).</summary>
    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SmartFileOrganizer");

    /// <summary>
    /// Resolves the database and settings paths.
    /// <list type="bullet">
    /// <item>Database: option, else <c>SFO_DB</c>, else <c>data.db</c> in the default data directory.</item>
    /// <item>Settings: option, else <c>SFO_SETTINGS</c>; otherwise <c>settings.json</c> next to the database
    /// when the database location was overridden, else in the default data directory.</item>
    /// </list>
    /// Keeping settings next to an overridden database means a test or scratch database never
    /// reads or writes the user's real settings by accident.
    /// </summary>
    public static SmartFileOrganizerPaths Resolve(
        SmartFileOrganizerHostOptions options,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        var dbOverride = NullIfBlank(options.DatabasePath)
            ?? NullIfBlank(getEnvironmentVariable(SmartFileOrganizerHostOptions.DatabasePathVariable));

        var databasePath = Path.GetFullPath(dbOverride ?? Path.Combine(DefaultDataDirectory, "data.db"));

        var settingsOverride = NullIfBlank(options.SettingsPath)
            ?? NullIfBlank(getEnvironmentVariable(SmartFileOrganizerHostOptions.SettingsPathVariable));

        var settingsPath = settingsOverride != null
            ? Path.GetFullPath(settingsOverride)
            : dbOverride != null
                ? Path.Combine(Path.GetDirectoryName(databasePath)!, "settings.json")
                : Path.Combine(DefaultDataDirectory, "settings.json");

        return new SmartFileOrganizerPaths(databasePath, settingsPath);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
