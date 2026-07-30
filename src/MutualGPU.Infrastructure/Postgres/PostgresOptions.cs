using Npgsql;

namespace MutualGPU.Infrastructure;

public sealed class PostgresOptions
{
    public string? ConnectionString { get; init; }

    public string? Host { get; init; }

    public int Port { get; init; } = 5432;

    public string Database { get; init; } = "mutualgpu";

    public string Username { get; init; } = "mutualgpu";

    public string? Password { get; init; }

    public int MinimumPoolSize { get; init; } = 1;

    public int MaximumPoolSize { get; init; } = 30;

    public int ConnectionTimeoutSeconds { get; init; } = 10;

    public int CommandTimeoutSeconds { get; init; } = 30;

    public bool RequireTls { get; init; }

    public string? HandleEncryptionKey { get; init; }

    public string BuildConnectionString()
    {
        var builder = String.IsNullOrWhiteSpace(ConnectionString)
            ? new NpgsqlConnectionStringBuilder
            {
                Host = Host,
                Port = Port,
                Database = Database,
                Username = Username,
                Password = Password,
            }
            : new NpgsqlConnectionStringBuilder(ConnectionString);

        builder.MinPoolSize = MinimumPoolSize;
        builder.MaxPoolSize = MaximumPoolSize;
        builder.Timeout = ConnectionTimeoutSeconds;
        builder.CommandTimeout = CommandTimeoutSeconds;
        builder.ApplicationName = "MutualGPU.Api";
        builder.IncludeErrorDetail = false;
        if (RequireTls)
        {
            builder.SslMode = SslMode.Require;
        }

        return builder.ConnectionString;
    }

    public void Validate(bool production)
    {
        if (String.IsNullOrWhiteSpace(ConnectionString) && String.IsNullOrWhiteSpace(Host))
        {
            throw new InvalidOperationException("MutualGPU:Postgres requires ConnectionString or Host.");
        }

        if (MinimumPoolSize < 0 || MaximumPoolSize < 1 || MinimumPoolSize > MaximumPoolSize)
        {
            throw new InvalidOperationException("MutualGPU:Postgres pool bounds are invalid.");
        }

        if (ConnectionTimeoutSeconds is < 1 or > 60 || CommandTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException("MutualGPU:Postgres timeout bounds are invalid.");
        }

        if (production && !RequireTls)
        {
            throw new InvalidOperationException("MutualGPU:Postgres:RequireTls must be true in production.");
        }

        _ = HandleCipher.ParseKey(HandleEncryptionKey);
    }
}
