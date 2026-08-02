using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MutualGPU.Application;
using MutualGPU.Contracts;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;
using Npgsql;

namespace MutualGPU.Api.Tests;

/// <summary>
/// Exercises the real HTTP composition over an isolated PostgreSQL 18 schema.
/// The artifact store stays in-memory because this is an API-host test, not a
/// cloud-storage test.
/// </summary>
public sealed class PostgresApiIntegrationTests
{
    private const string HandleEncryptionKey = "bXV0dWFsZ3B1LWxvY2FsLWRldmVsb3BtZW50LWtleSE=";

    [PostgresFact]
    public async Task Postgres_backed_http_host_migrates_an_empty_schema_and_survives_a_restart()
    {
        await using var schema = await IsolatedSchema.CreateAsync();
        var requestor = RequestorId.New();
        var capability = new CapabilityDefinition(
            CapabilityId.New(),
            "postgres-http-host",
            [],
            new OutputDefinition(),
            "postgres-http-host-contract");
        var task = new TaskRequest(
            TaskId.New(),
            requestor,
            capability,
            ResourceTier.Automatic,
            new TaskParameters(new Dictionary<string, string>(), null),
            DateTimeOffset.UtcNow);

        using (var first = CreateFactory(schema.ConnectionString))
        {
            using var client = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            using var ready = await client.GetAsync("/health/ready");
            Assert.True(ready.IsSuccessStatusCode);
            Assert.IsType<PostgresTaskStore>(first.Services.GetRequiredService<ITaskRepository>());
            await first.Services.GetRequiredService<IOperationUnitOfWork>().ExecuteAsync(
                (context, _) =>
                {
                    context.Capabilities.Add(capability);
                    context.Tasks.Add(task);
                    return Task.FromResult(true);
                },
                CancellationToken.None);
        }

        using var restarted = CreateFactory(schema.ConnectionString);
        using var restartedClient = restarted.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/tasks/");
        listRequest.Headers.Add("Cookie", $"{RequestorIdentity.CookieName}={requestor.Value:D}");
        using var listed = await restartedClient.SendAsync(listRequest, CancellationToken.None);
        var tasks = await listed.Content.ReadFromJsonAsync<TaskDto[]>();

        Assert.True(listed.IsSuccessStatusCode);
        Assert.Contains(tasks!, item => item.TaskId == task.Id.Value);
        Assert.IsType<PostgresTaskStore>(restarted.Services.GetRequiredService<ITaskRepository>());
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MutualGPU:Postgres:ConnectionString", connectionString);
            builder.UseSetting("MutualGPU:Postgres:HandleEncryptionKey", HandleEncryptionKey);
            builder.UseSetting("MutualGPU:ObjectStorage:WriteTargetId", ArtifactStorageTargetIds.AwsPrimary);
            builder.UseSetting("MutualGPU:ObjectStorage:SynchronizeBrowserCors", "false");
        });

    private sealed class IsolatedSchema : IAsyncDisposable
    {
        private readonly string rootConnectionString;

        private IsolatedSchema(string rootConnectionString, string name, string connectionString)
        {
            this.rootConnectionString = rootConnectionString;
            Name = name;
            ConnectionString = connectionString;
        }

        public string Name { get; }

        public string ConnectionString { get; }

        public static async Task<IsolatedSchema> CreateAsync()
        {
            var rootConnectionString = Environment.GetEnvironmentVariable("MUTUALGPU_TEST_POSTGRES")
                ?? throw new InvalidOperationException("MUTUALGPU_TEST_POSTGRES is required.");
            var name = $"postgres_api_{Guid.CreateVersion7():N}";
            await using (var connection = new NpgsqlConnection(rootConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"create schema \"{name}\"", connection);
                await command.ExecuteNonQueryAsync();
            }
            var builder = new NpgsqlConnectionStringBuilder(rootConnectionString) { SearchPath = name };
            return new IsolatedSchema(rootConnectionString, name, builder.ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(rootConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"drop schema if exists \"{Name}\" cascade", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUTUALGPU_TEST_POSTGRES")))
            {
                Skip = "Set MUTUALGPU_TEST_POSTGRES to run PostgreSQL integration tests.";
            }
        }
    }
}
