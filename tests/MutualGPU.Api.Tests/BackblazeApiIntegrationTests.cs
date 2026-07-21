using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Amazon.Runtime;
using Amazon.S3;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MutualGPU.Application;
using MutualGPU.Contracts;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;
using MutualGPU.Protocol;

namespace MutualGPU.Api.Tests;

public sealed class BackblazeApiIntegrationTests
{
    private static readonly ExecutionUnitId ProviderId = new(Guid.Parse("01816f5f-5678-7abc-8def-1234567890ab"));
    private const string ProviderKey = "backblaze-api-integration-provider-key";
    private const string BucketName = "mutualgpu-integration";

    [Fact]
    public async Task Configured_backblaze_host_becomes_ready_with_an_empty_bucket()
    {
        var storage = new S3Backend(BucketName);
        await using var host = CreateHost(storage);

        var objectStore = host.Services.GetRequiredService<IObjectStore>();
        Assert.IsType<BackblazeObjectStore>(objectStore);
        Assert.Same(objectStore, host.Services.GetRequiredService<IObjectStoreHealth>());

        using var client = CreateHttpsClient(host);
        using var readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.Empty(storage.Keys);
        Assert.True(storage.ListRequestCount > 0);
    }

    [Fact]
    public async Task Configured_backblaze_host_persists_api_state_across_restart()
    {
        var storage = new S3Backend(BucketName);
        var capability = new CapabilityDefinition(
            CapabilityId.New(),
            "backblaze-api-test",
            [],
            new OutputDefinition(),
            "provider-supplied-hash");
        var enrollment = new EnrollmentDefinition(
            new MachineProfile(ResourceTier.Medium, new MachineSpecifications(ResourceTier.Medium, 24)),
            [capability]);

        Guid taskId;
        string requestorCookie;
        await using (var firstHost = CreateHost(storage))
        {
            await firstHost.Services.GetRequiredService<ObjectStoreProviderKeyRegistry>().ProvisionAsync(ProviderId, ProviderKey, CancellationToken.None);
            using var client = CreateHttpsClient(firstHost);
            using var enrollmentRequest = new HttpRequestMessage(HttpMethod.Post, "/provider/enroll");
            enrollmentRequest.Headers.Authorization = new("Bearer", ProviderKey);
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

            var unit = await firstHost.Services.GetRequiredService<IExecutionUnitRepository>()
                .GetAsync(ProviderId, CancellationToken.None);
            Assert.NotNull(unit);
            firstHost.Services.GetRequiredService<ProviderConnectionRegistry>().Connect(unit);
            var storedCapability = Assert.Single(unit.CurrentEnrollment.Capabilities);

            var submission = new SubmitTaskRequestDto(
                storedCapability.Id.Value,
                storedCapability.ContractHash,
                new Dictionary<string, string>(),
                new MachineSpecifications(ResourceTier.Medium, 16),
                "backblaze-restart-idempotency-key");

            using var submitted = await client.PostAsJsonAsync("/api/tasks/", submission);
            Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);
            var task = await submitted.Content.ReadFromJsonAsync<TaskDto>();
            Assert.NotNull(task);
            taskId = task.TaskId;

            Assert.Contains(storage.Keys, key => key.EndsWith("/identity.json", StringComparison.Ordinal));
            Assert.Contains(storage.Keys, key => key.EndsWith("/manifest.json", StringComparison.Ordinal));
            Assert.Contains(storage.Keys, key => key.Contains("/facts/", StringComparison.Ordinal));
        }

        await using (var restartedHost = CreateHost(storage))
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

        Assert.True(storage.ListRequestCount >= 2);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static WebApplicationFactory<Program> CreateHost(S3Backend storage) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MutualGPU:Backblaze:Endpoint", "https://s3.us-west-000.backblazeb2.com");
            builder.UseSetting("MutualGPU:Backblaze:KeyId", "integration-key-id");
            builder.UseSetting("MutualGPU:Backblaze:ApplicationKey", "integration-application-key");
            builder.UseSetting("MutualGPU:Backblaze:BucketName", BucketName);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAmazonS3>();
                services.AddSingleton<IAmazonS3>(_ => storage.CreateClient());
            });
        });

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> host, bool handleCookies = true) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = handleCookies,
        });

    private sealed class S3Backend(string bucketName) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, StoredObject> objects = new(StringComparer.Ordinal);
        private int listRequestCount;

        public IEnumerable<string> Keys => objects.Keys;
        public int ListRequestCount => Volatile.Read(ref listRequestCount);

        public IAmazonS3 CreateClient() => new AmazonS3Client(
            new BasicAWSCredentials("integration-key-id", "integration-application-key"),
            new AmazonS3Config
            {
                ServiceURL = "https://s3.us-west-000.backblazeb2.com",
                ForcePathStyle = true,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                HttpClientFactory = new TestHttpClientFactory(this),
            });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (bucket, key) = ParsePath(request.RequestUri!);
            if (!StringComparer.Ordinal.Equals(bucket, bucketName))
            {
                return Error(HttpStatusCode.NotFound, "NoSuchBucket", "The requested bucket does not exist.");
            }

            if (request.Method == HttpMethod.Get && key is null)
            {
                Interlocked.Increment(ref listRequestCount);
                return List(request.RequestUri!);
            }

            if (key is null)
            {
                return Error(HttpStatusCode.BadRequest, "InvalidRequest", "An object key is required.");
            }

            if (request.Method == HttpMethod.Put)
            {
                if (request.Headers.IfNoneMatch.Any() && objects.ContainsKey(key))
                {
                    return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed", "The object already exists.");
                }

                if (request.Headers.IfMatch.Count > 0 &&
                    (!objects.TryGetValue(key, out var current) ||
                     !request.Headers.IfMatch.Any(candidate => StringComparer.Ordinal.Equals(candidate.Tag, current.ETag))))
                {
                    return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed", "The ETag did not match.");
                }

                var transmitted = request.Content is null
                    ? []
                    : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var bytes = request.Content?.Headers.ContentEncoding.Contains("aws-chunked", StringComparer.OrdinalIgnoreCase) is true
                    ? DecodeAwsChunked(transmitted)
                    : transmitted;
                var etag = $"\"{Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant()}\"";
                objects[key] = new StoredObject(
                    bytes,
                    request.Content?.Headers.ContentType?.ToString() ?? "application/octet-stream",
                    etag,
                    DateTimeOffset.UtcNow);
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.ETag = new(etag);
                return response;
            }

            if (request.Method == HttpMethod.Get)
            {
                if (!objects.TryGetValue(key, out var stored))
                {
                    return Error(HttpStatusCode.NotFound, "NoSuchKey", "The requested key does not exist.");
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(stored.Content),
                };
                response.Content.Headers.ContentType = new(stored.ContentType);
                response.Headers.ETag = new(stored.ETag);
                response.Content.Headers.LastModified = stored.LastModified;
                return response;
            }

            if (request.Method == HttpMethod.Delete)
            {
                objects.TryRemove(key, out _);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed", "The method is not supported by this test endpoint.");
        }

        private HttpResponseMessage List(Uri uri)
        {
            var prefix = QueryValue(uri, "prefix") ?? String.Empty;
            XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
            var matches = objects
                .Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .ToArray();
            var document = new XDocument(
                new XElement(ns + "ListBucketResult",
                    new XElement(ns + "Name", bucketName),
                    new XElement(ns + "Prefix", prefix),
                    new XElement(ns + "KeyCount", matches.Length),
                    new XElement(ns + "MaxKeys", 1000),
                    new XElement(ns + "IsTruncated", false),
                    matches.Select(item => new XElement(ns + "Contents",
                        new XElement(ns + "Key", item.Key),
                        new XElement(ns + "LastModified", item.Value.LastModified.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                        new XElement(ns + "ETag", item.Value.ETag),
                        new XElement(ns + "Size", item.Value.Content.LongLength),
                        new XElement(ns + "StorageClass", "STANDARD")))));
            return Xml(HttpStatusCode.OK, document);
        }

        private static (string? Bucket, string? Key) ParsePath(Uri uri)
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
            return segments.Length switch
            {
                0 => (null, null),
                1 => (Uri.UnescapeDataString(segments[0]), null),
                _ => (Uri.UnescapeDataString(segments[0]), Uri.UnescapeDataString(segments[1])),
            };
        }

        private static string? QueryValue(Uri uri, string name)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (StringComparer.Ordinal.Equals(Uri.UnescapeDataString(parts[0]), name))
                {
                    return parts.Length == 1 ? String.Empty : Uri.UnescapeDataString(parts[1].Replace('+', ' '));
                }
            }
            return null;
        }

        private static byte[] DecodeAwsChunked(byte[] content)
        {
            using var decoded = new MemoryStream();
            var offset = 0;
            while (offset < content.Length)
            {
                var lineEnd = content.AsSpan(offset).IndexOf("\r\n"u8);
                if (lineEnd < 0) throw new InvalidDataException("The aws-chunked request has an incomplete chunk header.");
                var header = Encoding.ASCII.GetString(content, offset, lineEnd);
                var extension = header.IndexOf(';');
                var length = Int32.Parse(extension < 0 ? header : header[..extension], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                offset += lineEnd + 2;
                if (length == 0) break;
                if (offset + length + 2 > content.Length) throw new InvalidDataException("The aws-chunked request has an incomplete payload.");
                decoded.Write(content, offset, length);
                offset += length + 2;
            }
            return decoded.ToArray();
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
            Xml(status, new XDocument(new XElement("Error", new XElement("Code", code), new XElement("Message", message))));

        private static HttpResponseMessage Xml(HttpStatusCode status, XDocument document) => new(status)
        {
            Content = new StringContent(document.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml"),
        };

        private sealed record StoredObject(byte[] Content, string ContentType, string ETag, DateTimeOffset LastModified);

        private sealed class TestHttpClientFactory(HttpMessageHandler handler) : Amazon.Runtime.HttpClientFactory
        {
            public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);

            public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

            public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
        }
    }
}
