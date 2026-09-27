using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

/// <summary>
/// Where enquiries are stored: a local SQLite file by default, or PostgreSQL when
/// BOXWOOD_DATABASE_URL is set (used on hosts whose free plans have no persistent disk).
/// Queries use @name parameters and INSERT ... RETURNING, which both databases understand.
/// </summary>
public sealed class EnquiryDatabase
{
    private readonly string connectionString;

    private EnquiryDatabase(bool isPostgres, string connectionString, string description)
    {
        IsPostgres = isPostgres;
        this.connectionString = connectionString;
        Description = description;
    }

    public bool IsPostgres { get; }

    /// <summary>Safe to log: never contains the password.</summary>
    public string Description { get; }

    public static EnquiryDatabase Sqlite(string databasePath) =>
        new(false, $"Data Source={databasePath}", $"SQLite file {databasePath}");

    /// <param name="url">postgres://user:password@host[:port]/database[?sslmode=...], or an Npgsql key=value string.</param>
    public static EnquiryDatabase Postgres(string url)
    {
        var builder = url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
                      || url.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
            ? FromUrl(new Uri(url))
            : new NpgsqlConnectionStringBuilder(url);

        // Free Postgres hosts suspend idle databases and drop their connections, so do not keep
        // pooled connections long enough to go stale, and allow time for the database to wake up.
        builder.ConnectionIdleLifetime = 30;
        builder.ConnectionPruningInterval = 10;
        builder.Timeout = 30;
        builder.ApplicationName = "boxwood-api";
        return new EnquiryDatabase(true, builder.ConnectionString, $"PostgreSQL database {builder.Database} on {builder.Host}");
    }

    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        DbConnection connection = IsPostgres
            ? new NpgsqlConnection(connectionString)
            : new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public static DbCommand Command(DbConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    public async Task InitialiseAsync()
    {
        var idColumn = IsPostgres
            ? "Id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY"
            : "Id INTEGER PRIMARY KEY AUTOINCREMENT";
        await using var connection = await OpenAsync();
        await using var command = Command(connection, $@"
            CREATE TABLE IF NOT EXISTS Enquiries (
                {idColumn},
                GuestName TEXT NOT NULL,
                GuestEmail TEXT NOT NULL,
                GuestNote TEXT,
                GameChoice TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                ClientKey TEXT NOT NULL
            );");
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Rewrites a SQLite file so deleted or replaced plaintext does not linger in free pages.</summary>
    public async Task CompactAsync(DbConnection connection, CancellationToken cancellationToken = default)
    {
        if (!IsPostgres)
        {
            await using var vacuum = Command(connection, "VACUUM;");
            await vacuum.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public string SqliteConnectionString =>
        IsPostgres ? throw new InvalidOperationException("Not a SQLite database.") : connectionString;

    private static NpgsqlConnectionStringBuilder FromUrl(Uri uri)
    {
        var credentials = uri.UserInfo.Split(':', 2);
        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : "",
                StringComparer.OrdinalIgnoreCase);

        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : null,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            // Guest data crosses the internet here, so require TLS and check the server certificate.
            // Only an explicit sslmode=disable (for a local test database) turns it off.
            SslMode = query.TryGetValue("sslmode", out var mode) && mode.Equals("disable", StringComparison.OrdinalIgnoreCase)
                ? SslMode.Disable
                : SslMode.VerifyFull,
        };
    }
}
