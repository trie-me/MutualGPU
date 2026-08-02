using MutualGPU.Application;
using MutualGPU.Domain;
using Npgsql;

namespace MutualGPU.Infrastructure;

public static class PostgresDataSourceFactory
{
    public static NpgsqlDataSource Create(PostgresOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new NpgsqlDataSourceBuilder(options.BuildConnectionString());
        builder.MapEnum<MutualGPU.Domain.TaskStatus>("task_status");
        builder.MapEnum<AttemptState>("attempt_state");
        builder.MapEnum<ArtifactDirection>("artifact_direction");
        builder.MapEnum<ArtifactState>("artifact_state");
        builder.MapEnum<ArtifactLocationState>("artifact_location_state");
        builder.MapEnum<ResultUploadState>("result_upload_state");
        return builder.Build();
    }
}
