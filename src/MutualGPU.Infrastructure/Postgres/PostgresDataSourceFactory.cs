using MutualGPU.Application;
using MutualGPU.Domain;
using Npgsql;

namespace MutualGPU.Infrastructure;

public static class PostgresDataSourceFactory
{
    public static NpgsqlDataSource Create(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connectionString = options.BuildConnectionString();
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        // Tests and maintenance tools use an isolated search-path schema. Npgsql
        // otherwise resolves duplicate enum names to public, causing values for
        // the selected schema's tables to have the wrong PostgreSQL enum type.
        var schema = connection.SearchPath?
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "public";
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.MapEnum<MutualGPU.Domain.TaskStatus>($"{schema}.task_status");
        builder.MapEnum<AttemptState>($"{schema}.attempt_state");
        builder.MapEnum<ArtifactDirection>($"{schema}.artifact_direction");
        builder.MapEnum<ArtifactState>($"{schema}.artifact_state");
        builder.MapEnum<ArtifactLocationState>($"{schema}.artifact_location_state");
        builder.MapEnum<ResultUploadState>($"{schema}.result_upload_state");
        return builder.Build();
    }
}
