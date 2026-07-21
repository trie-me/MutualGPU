using System.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MutualGPU.Application;
using MutualGPU.Contracts;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;
using MutualGPU.Protocol;

namespace MutualGPU.Api.Tests;

/// <summary>
/// Opt-in verification of Backblaze B2's S3-compatible behavior. Secrets are supplied only as process environment variables.
/// </summary>
public sealed class LiveBackblazeIntegrationTests
{
    [LiveBackblazeFact]
    public async Task Backblaze_adapter_round_trips_conditional_objects_and_presigned_urls()
    {
        var settings = LiveBackblazeSettings.FromEnvironmentOrThrow();
        using var services = CreateServices(settings);
        var store = services.GetRequiredService<IObjectStore>();
        var runPrefix = new ObjectPrefix($"integration/{Guid.CreateVersion7():N}");
        var key = new ObjectKey($"{runPrefix.Value}/payload.bin");
        var bytes = Encoding.UTF8.GetBytes("mutualgpu-backblaze-contract");

        try
        {
            Assert.Null(await store.GetAsync(key, CancellationToken.None));

            await using (var payload = new MemoryStream(bytes, writable: false))
            {
                await store.PutAsync(key, payload, ObjectWriteConditions.IfNotExists, CancellationToken.None);
            }
            await using (var duplicate = new MemoryStream(bytes, writable: false))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    store.PutAsync(key, duplicate, ObjectWriteConditions.IfNotExists, CancellationToken.None));
            }

            await using (var read = await store.GetAsync(key, CancellationToken.None))
            {
                Assert.NotNull(read);
                Assert.Equal(bytes.LongLength, read.Length);
                using var content = new MemoryStream();
                await read.Content.CopyToAsync(content, CancellationToken.None);
                Assert.Equal(bytes, content.ToArray());
                Assert.False(String.IsNullOrWhiteSpace(read.ETag));
                var originalETag = read.ETag;

                await using var replacement = new MemoryStream(Encoding.UTF8.GetBytes("replacement"), writable: false);
                await store.PutAsync(key, replacement, new ObjectWriteConditions(ExpectedETag: read.ETag), CancellationToken.None);
                await using var staleReplacement = new MemoryStream(Encoding.UTF8.GetBytes("stale replacement"), writable: false);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    store.PutAsync(key, staleReplacement, new ObjectWriteConditions(ExpectedETag: originalETag), CancellationToken.None));
            }

            var entries = new List<ObjectEntry>();
            await foreach (var entry in store.ListAsync(runPrefix, CancellationToken.None)) entries.Add(entry);
            Assert.Contains(entries, entry => entry.Key == key);

            var url = await store.CreateDownloadUrlAsync(key, TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.Equal(Uri.UriSchemeHttps, url.Scheme);
            Assert.Contains(settings.BucketName, url.AbsoluteUri, StringComparison.Ordinal);
        }
        finally
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }

        Assert.Null(await store.GetAsync(key, CancellationToken.None));
    }

    [LiveBackblazeFact]
    public async Task Configured_api_becomes_ready_against_an_empty_live_bucket()
    {
        var settings = LiveBackblazeSettings.FromEnvironmentOrThrow();
        using var storageServices = CreateServices(settings);
        var store = storageServices.GetRequiredService<IObjectStore>();
        await foreach (var _ in store.ListAsync(new ObjectPrefix("mutualgpu/v3"), CancellationToken.None))
        {
            throw new InvalidOperationException("The live API readiness test requires a dedicated empty bucket.");
        }

        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MutualGPU:Backblaze:Endpoint", settings.Endpoint);
            builder.UseSetting("MutualGPU:Backblaze:KeyId", settings.KeyId);
            builder.UseSetting("MutualGPU:Backblaze:ApplicationKey", settings.ApplicationKey);
            builder.UseSetting("MutualGPU:Backblaze:BucketName", settings.BucketName);
        });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var readiness = await client.GetAsync("/health/ready");

        Assert.True(readiness.IsSuccessStatusCode);
        Assert.IsType<BackblazeObjectStore>(host.Services.GetRequiredService<IObjectStore>());
    }

    [LiveBackblazeFact]
    public async Task Configured_api_persists_enrollment_and_task_across_a_live_backblaze_restart()
    {
        var settings = LiveBackblazeSettings.FromEnvironmentOrThrow();
        using var storageServices = CreateServices(settings);
        var store = storageServices.GetRequiredService<IObjectStore>();
        var root = new ObjectPrefix("mutualgpu/v3");
        await EnsureEmptyAsync(store, root);

        var providerId = new ExecutionUnitId(Guid.CreateVersion7());
        var providerKey = Guid.NewGuid().ToString("N");
        var capability = new CapabilityDefinition(CapabilityId.New(), "live-backblaze-api", [], new OutputDefinition(), "provider-hash");
        var enrollment = new EnrollmentDefinition(
            new MachineProfile(ResourceTier.Medium, new MachineSpecifications(ResourceTier.Medium, 24)),
            [capability]);
        string requestorCookie = String.Empty;
        Guid taskId = Guid.Empty;

        try
        {
            await using (var host = CreateLiveHost(settings, providerId, providerKey))
            {
                using var client = CreateHttpsClient(host);
                using var enrollmentRequest = new HttpRequestMessage(HttpMethod.Post, "/provider/enroll");
                enrollmentRequest.Headers.Authorization = new("Bearer", providerKey);
                enrollmentRequest.Content = new ByteArrayContent(new EnrollRequest
                {
                    Definition = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(enrollment, JsonOptions)),
                }.ToByteArray());
                enrollmentRequest.Content.Headers.ContentType = new("application/x-protobuf");
                using var enrollmentResponse = await client.SendAsync(enrollmentRequest, CancellationToken.None);
                Assert.Equal(HttpStatusCode.OK, enrollmentResponse.StatusCode);
                requestorCookie = Assert.Single(
                    enrollmentResponse.Headers.GetValues("Set-Cookie"),
                    value => value.StartsWith($"{RequestorIdentity.CookieName}=", StringComparison.Ordinal))
                    .Split(';', 2)[0];

                var unit = await host.Services.GetRequiredService<IExecutionUnitRepository>().GetAsync(providerId, CancellationToken.None);
                Assert.NotNull(unit);
                host.Services.GetRequiredService<ProviderConnectionRegistry>().Connect(unit);
                var storedCapability = Assert.Single(unit.CurrentEnrollment.Capabilities);
                var submission = new SubmitTaskRequestDto(
                    storedCapability.Id.Value,
                    storedCapability.ContractHash,
                    new Dictionary<string, string>(),
                    new MachineSpecifications(ResourceTier.Medium, 16),
                    "live-backblaze-restart-idempotency-key");
                using var submitted = await client.PostAsJsonAsync("/api/tasks/", submission);
                Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
                var task = await submitted.Content.ReadFromJsonAsync<TaskDto>();
                Assert.NotNull(task);
                taskId = task.TaskId;
            }

            await using (var restartedHost = CreateLiveHost(settings, providerId, providerKey))
            {
                using var client = CreateHttpsClient(restartedHost, handleCookies: false);
                using var readiness = await client.GetAsync("/health/ready");
                Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
                using var tasksRequest = new HttpRequestMessage(HttpMethod.Get, "/api/tasks/");
                tasksRequest.Headers.Add("Cookie", requestorCookie);
                using var tasksResponse = await client.SendAsync(tasksRequest, CancellationToken.None);
                Assert.Equal(HttpStatusCode.OK, tasksResponse.StatusCode);
                var tasks = await tasksResponse.Content.ReadFromJsonAsync<TaskDto[]>();
                Assert.Contains(tasks!, task => task.TaskId == taskId && task.Status is MutualGPU.Domain.TaskStatus.Queued);
            }
        }
        finally
        {
            await DeleteAllAsync(store, root);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static WebApplicationFactory<Program> CreateLiveHost(LiveBackblazeSettings settings, ExecutionUnitId providerId, string providerKey) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MutualGPU:Backblaze:Endpoint", settings.Endpoint);
            builder.UseSetting("MutualGPU:Backblaze:KeyId", settings.KeyId);
            builder.UseSetting("MutualGPU:Backblaze:ApplicationKey", settings.ApplicationKey);
            builder.UseSetting("MutualGPU:Backblaze:BucketName", settings.BucketName);
            builder.UseSetting("MutualGPU:Providers:0:ExecutionUnitId", providerId.Value.ToString("D"));
            builder.UseSetting("MutualGPU:Providers:0:PresharedKey", providerKey);
        });

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> host, bool handleCookies = true) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = handleCookies,
        });

    private static async Task EnsureEmptyAsync(IObjectStore store, ObjectPrefix root)
    {
        await foreach (var _ in store.ListAsync(root, CancellationToken.None))
        {
            throw new InvalidOperationException("Live API persistence tests require an empty MutualGPU object root.");
        }
    }

    private static async Task DeleteAllAsync(IObjectStore store, ObjectPrefix root)
    {
        var keys = new List<ObjectKey>();
        await foreach (var entry in store.ListAsync(root, CancellationToken.None)) keys.Add(entry.Key);
        foreach (var key in keys)
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
    }

    private static ServiceProvider CreateServices(LiveBackblazeSettings settings)
    {
        var collection = new ServiceCollection();
        collection.AddMutualGpuBackblazeObjectStore(new BackblazeS3Options(
            settings.Endpoint,
            settings.KeyId,
            settings.ApplicationKey,
            settings.BucketName));
        return collection.BuildServiceProvider();
    }

    private sealed record LiveBackblazeSettings(string Endpoint, string KeyId, string ApplicationKey, string BucketName)
    {
        public static bool IsConfigured() =>
            !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUTUALGPU_BACKBLAZE_ENDPOINT")) &&
            !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUTUALGPU_BACKBLAZE_KEY_ID")) &&
            !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUTUALGPU_BACKBLAZE_APPLICATION_KEY")) &&
            !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MUTUALGPU_BACKBLAZE_BUCKET_NAME"));

        public static LiveBackblazeSettings FromEnvironmentOrThrow()
        {
            var endpoint = Environment.GetEnvironmentVariable("MUTUALGPU_BACKBLAZE_ENDPOINT");
            var keyId = Environment.GetEnvironmentVariable("MUTUALGPU_BACKBLAZE_KEY_ID");
            var applicationKey = Environment.GetEnvironmentVariable("MUTUALGPU_BACKBLAZE_APPLICATION_KEY");
            var bucketName = Environment.GetEnvironmentVariable("MUTUALGPU_BACKBLAZE_BUCKET_NAME");
            if (String.IsNullOrWhiteSpace(endpoint) || String.IsNullOrWhiteSpace(keyId) ||
                String.IsNullOrWhiteSpace(applicationKey) || String.IsNullOrWhiteSpace(bucketName))
            {
                throw new InvalidOperationException("Live Backblaze settings are unavailable.");
            }

            return new LiveBackblazeSettings(endpoint, keyId, applicationKey, bucketName);
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    private sealed class LiveBackblazeFactAttribute : FactAttribute
    {
        public LiveBackblazeFactAttribute()
        {
            if (!LiveBackblazeSettings.IsConfigured())
            {
                Skip = "Set MUTUALGPU_BACKBLAZE_ENDPOINT, MUTUALGPU_BACKBLAZE_KEY_ID, MUTUALGPU_BACKBLAZE_APPLICATION_KEY, and MUTUALGPU_BACKBLAZE_BUCKET_NAME to run live Backblaze tests.";
            }
        }
    }
}
