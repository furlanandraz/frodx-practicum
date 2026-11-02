using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace OrderIngestion.Infrastructure.Persistence;

internal sealed class SqliteDatabase(IOptions<DatabaseOptions> options)
{
    private readonly string _filePath = options.Value.FilePath;

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = options.Value.FilePath,
        DefaultTimeout = 30,
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

        await connection.ExecuteAsync("PRAGMA journal_mode = WAL;");
        await connection.ExecuteAsync(SqliteSchema.Sql);
    }
}
