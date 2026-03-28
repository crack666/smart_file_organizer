using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// Manages the SQLite database lifecycle: initialization, schema creation, versioning.
/// </summary>
public class DatabaseContext
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseContext> _logger;

    public DatabaseContext(string dbPath, ILogger<DatabaseContext> logger)
    {
        _connectionString = $"Data Source={dbPath};";
        _logger = logger;
    }

    public SqliteConnection CreateConnection() => new(_connectionString);

    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing database at {ConnectionString}", _connectionString);

        await using var conn = CreateConnection();
        await conn.OpenAsync();

        await EnsureSchemaAsync(conn);
    }

    private static async Task EnsureSchemaAsync(SqliteConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema.CreateAll;
        await cmd.ExecuteNonQueryAsync();
    }
}
