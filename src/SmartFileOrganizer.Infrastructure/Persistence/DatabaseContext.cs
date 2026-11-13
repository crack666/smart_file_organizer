using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SmartFileOrganizer.Infrastructure.Persistence.Migrations;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// Manages the SQLite database lifecycle: connection creation and schema migration.
/// </summary>
public class DatabaseContext
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseContext> _logger;

    static DatabaseContext()
    {
        // Dapper: map snake_case column names to PascalCase C# properties.
        // Set here so every composition (GUI, CLI, tests) gets the same mapping.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new SensitiveMarkerTypeHandler());
        // ai_results is read through ClassificationRepository.Row, which parses the codes itself.
        // Skipped folders carry markers as well (directory_nodes.sensitive_markers, also read into DirectoryReadRow).
        foreach (var type in new[] { typeof(Domain.Models.FileNode), typeof(Domain.Models.DirectoryNode), typeof(Domain.Models.DirectoryReadRow) })
            SqlMapper.SetTypeMap(type, new SensitiveMarkerColumnMap(type));
    }

    public DatabaseContext(string dbPath, ILogger<DatabaseContext> logger)
    {
        DatabasePath = dbPath;
        // PRAGMA foreign_keys applies to one connection only, and every repository call opens a new one.
        // "Foreign Keys=True" makes Microsoft.Data.Sqlite run the pragma on each open.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false,
            ForeignKeys = true
        }.ToString();
        _logger = logger;
    }

    public string DatabasePath { get; }

    public SqliteConnection CreateConnection() => new(_connectionString);

    /// <summary>
    /// Opens the database (creating the file if needed) and applies all pending schema steps.
    /// Returns the schema version afterwards.
    /// </summary>
    public async Task<int> InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Initializing database at {DatabasePath}", DatabasePath);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        // journal_mode cannot change inside a transaction, so it runs before the migrator.
        // The migrator switches foreign keys off for its steps and on again afterwards.
        await conn.ExecuteAsync(new CommandDefinition(
            "PRAGMA journal_mode=WAL;", cancellationToken: ct));

        var migrator = new SchemaMigrator(SchemaMigrator.BuiltIn, _logger);
        var version = await migrator.MigrateAsync(conn, ct);

        _logger.LogInformation("Database schema at version {Version}", version);
        return version;
    }
}
