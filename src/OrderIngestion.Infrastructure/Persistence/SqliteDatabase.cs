using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace OrderIngestion.Infrastructure.Persistence;

/// <summary>Opens connections to the SQLite file and creates the schema.</summary>
internal sealed class SqliteDatabase(IOptions<DatabaseOptions> options)
{
    private readonly string _filePath = options.Value.FilePath;

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = options.Value.FilePath,
        ForeignKeys = true,   // SQLite ignores REFERENCES unless this is on
        DefaultTimeout = 30,  // seconds to wait for a lock before failing
    }.ToString();

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken);

        // Write-ahead logging: readers (e.g. a future health check) don't block the writer.
        await connection.ExecuteAsync("PRAGMA journal_mode = WAL;");
        await connection.ExecuteAsync(SqliteSchema.Sql);
    }
}
