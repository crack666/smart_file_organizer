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
            ForeignKeys = true,
            // Microsoft.Data.Sqlite retries a locked database until the command timeout. The default
            // of 30 s was too short for parallel writers on a slow disk: on 14.11.2026 three CI runs
            // on Windows ended with "database is locked".
            DefaultTimeout = LockWaitSeconds
        }.ToString();
        _logger = logger;
    }

    /// <summary>How long a command waits for a locked database before it fails.</summary>
    public const int LockWaitSeconds = 180;

    public string DatabasePath { get; }

    public SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        // synchronous is a setting of the connection, not of the file. NORMAL is safe with WAL (the
        // file stays consistent; only the last commits before a power cut can be lost) and saves a
        // disk flush per commit, which is what made each writer hold the lock so long.
        connection.StateChange += (sender, change) =>
        {
            if (change.CurrentState != System.Data.ConnectionState.Open || sender is not SqliteConnection opened)
                return;
            using var command = opened.CreateCommand();
            command.CommandText = "PRAGMA synchronous=NORMAL;";
            command.ExecuteNonQuery();
        };
        return connection;
    }

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
