using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MutualGPU.Application;
using MutualGPU.Contracts;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;
using MutualGPU.Protocol;

namespace MutualGPU.Api.Tests;

public sealed class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly ExecutionUnitId ProviderId = new(Guid.Parse("01816f5f-1234-7abc-8def-1234567890ab"));
    private const string ProviderKey = "api-integration-provider-key";
    private readonly WebApplicationFactory<Program> factory;

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("NetCats:FiberDiagnostics:Enabled", "true");
            builder.UseSetting("MutualGPU:Providers:0:ExecutionUnitId", ProviderId.Value.ToString("D"));
            builder.UseSetting("MutualGPU:Providers:0:PresharedKey", ProviderKey);
            builder.UseSetting("MutualGPU:ProviderCorsOrigins:0", "https://provider.example");
        });
    }

    [Fact]
    public async Task Host_serves_liveness_static_ui_and_an_initial_fiber_snapshot()
    {
        using var client = CreateHttpsClient();

        var health = await client.GetAsync("/health/live");
        var readiness = await client.GetAsync("/health/ready");
        var openApi = await client.GetAsync("/openapi/v1.json");
        using var insecureClient = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        var insecureHealth = await insecureClient.GetAsync("/health/live");
        var page = await client.GetStringAsync("/");
        var snapshot = await client.GetAsync("/_netcats/fibers/snapshot");

        Assert.True(health.IsSuccessStatusCode);
        Assert.True(readiness.IsSuccessStatusCode);
        Assert.True(openApi.IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, insecureHealth.StatusCode);
        Assert.Contains("MutualGPU", page, StringComparison.Ordinal);
        Assert.Contains("resource-picker", page, StringComparison.Ordinal);
        Assert.Contains("/css/site.css", page, StringComparison.Ordinal);
        Assert.Contains("/css/fiber-tree-overlay.css", page, StringComparison.Ordinal);
        Assert.Contains("class=\"layout\"", page, StringComparison.Ordinal);
        Assert.Contains("fiber-overlay", page, StringComparison.Ordinal);
        Assert.True(snapshot.IsSuccessStatusCode);
        Assert.Contains("roots", await snapshot.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fiber_stream_starts_with_a_complete_snapshot()
    {
        using var client = CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_netcats/fibers/stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.True(response.IsSuccessStatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal("event: snapshot", await reader.ReadLineAsync());
        var data = await reader.ReadLineAsync();
        Assert.NotNull(data);
        Assert.StartsWith("data: {", data, StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_matrix_module_is_shipped_with_the_api()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/MutualGPU.Api/wwwroot"));
        var module = Path.Combine(root, "js", "resource-grid.js");
        var overlay = Path.Combine(root, "js", "fiber-tree-overlay.js");
        var form = Path.Combine(root, "js", "capability-form.js");
        var tasks = Path.Combine(root, "js", "task-list.js");

        Assert.True(File.Exists(module));
        Assert.True(File.Exists(overlay));
        Assert.True(File.Exists(form));
        Assert.True(File.Exists(tasks));
        var source = File.ReadAllText(module);
        Assert.Contains("renderResourceGrid", source, StringComparison.Ordinal);
        Assert.Contains("resource-tile", source, StringComparison.Ordinal);
        Assert.Contains("createFiberDiagnosticsOverlay", File.ReadAllText(overlay), StringComparison.Ordinal);
        Assert.Contains("createScalarPayload", File.ReadAllText(form), StringComparison.Ordinal);
        Assert.Contains("renderTaskList", File.ReadAllText(tasks), StringComparison.Ordinal);
    }

    [Fact]
    public void Frontend_reuses_the_purrfectseat_visual_system()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/MutualGPU.Api/wwwroot"));
        var site = File.ReadAllText(Path.Combine(root, "css", "site.css"));

        Assert.Contains("--navy:#111b38", site, StringComparison.Ordinal);
        Assert.Contains(".layout { display:grid", site, StringComparison.Ordinal);
        Assert.Contains(".card { background:white", site, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_resource_tiers_serialize_as_named_strings()
    {
        var task = new TaskDto(
            Guid.CreateVersion7(), "Example", DateTimeOffset.UnixEpoch,
            new MachineSpecifications(ResourceTier.ExtraLarge, 128), MutualGPU.Domain.TaskStatus.Queued,
            0, null, true, false);

        var json = JsonSerializer.Serialize(task, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"computeTier\":\"ExtraLarge\"", json, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"Queued\"", json, StringComparison.Ordinal);

        var submitted = JsonSerializer.Deserialize<SubmitTaskRequestDto>("""
            {"capabilityId":"00000000-0000-0000-0000-000000000001","contractHash":"example","scalars":{},"resources":{"computeTier":"Large","memoryGiB":32}}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(submitted);
        Assert.Equal(new MachineSpecifications(ResourceTier.Large, 32), submitted.Resources);
    }

    [Theory]
    [MemberData(nameof(WebpHeaders))]
    public void Image_validation_accepts_standard_webp_container_variants(byte[] image)
    {
        Assert.True(MutualGpuEndpoints.TryValidateImage("image/webp", image, out var extension));
        Assert.Equal("webp", extension);
    }

    public static IEnumerable<object[]> WebpHeaders()
    {
        // 512 × 256 VP8 lossy frame header.
        yield return [new byte[] { 82, 73, 70, 70, 22, 0, 0, 0, 87, 69, 66, 80, 86, 80, 56, 32, 10, 0, 0, 0, 0, 0, 0, 157, 1, 42, 0, 2, 0, 1 }];
        // 512 × 256 VP8L lossless frame header.
        yield return [new byte[] { 82, 73, 70, 70, 18, 0, 0, 0, 87, 69, 66, 80, 86, 80, 56, 76, 5, 0, 0, 0, 47, 255, 193, 63, 0 }];
    }

    [Fact]
    public async Task Configured_browser_provider_origin_can_preflight_authenticated_result_upload()
    {
        using var client = CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/provider/tasks/00000000-0000-0000-0000-000000000000/attempts/00000000-0000-0000-0000-000000000000/result");
        request.Headers.Add("Origin", "https://provider.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,x-mutualgpu-task-handle,x-mutualgpu-upload-token,x-mutualgpu-sha256");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("https://provider.example", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Requestor_cookie_can_submit_a_resource_profile_and_list_the_idempotent_task()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "requestor-api-test", [], new OutputDefinition(), "requestor-contract");
        var unit = new ExecutionUnit(ProviderId, new EnrollmentDefinition(Machine(ResourceTier.Large, ResourceTier.Large, 32), [capability]));
        var units = factory.Services.GetRequiredService<IExecutionUnitRepository>();
        var connections = factory.Services.GetRequiredService<ProviderConnectionRegistry>();
        await units.SaveAsync(unit, CancellationToken.None);
        connections.Connect(unit);
        using var client = CreateHttpsClient();
        using var landing = await client.GetAsync("/"); // establishes the anonymous requestor cookie
        Assert.True(landing.IsSuccessStatusCode);

        var catalogue = await client.GetFromJsonAsync<CapabilityAvailabilityDto[]>("/api/capabilities/");
        var available = Assert.Single(catalogue!, item => item.CapabilityId == capability.Id.Value);
        Assert.Contains(available.MachineAvailability, item => item.ComputeTier is ResourceTier.Large && item.MemoryGiB == 32);
        var submission = new SubmitTaskRequestDto(capability.Id.Value, capability.ContractHash, new Dictionary<string, string>(), new MachineSpecifications(ResourceTier.Large, 32), "requestor-idempotency-key");

        using var createdResponse = await client.PostAsJsonAsync("/api/tasks/", submission);
        Assert.Equal(System.Net.HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<TaskDto>();
        Assert.NotNull(created);
        Assert.Equal(new MachineSpecifications(ResourceTier.Large, 32), created.Resources);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Queued, created.Status);

        using var repeatedResponse = await client.PostAsJsonAsync("/api/tasks/", submission);
        var repeated = await repeatedResponse.Content.ReadFromJsonAsync<TaskDto>();
        Assert.Equal(created.TaskId, repeated!.TaskId);
        var listed = await client.GetFromJsonAsync<TaskDto[]>("/api/tasks/");
        Assert.Contains(listed!, task => task.TaskId == created.TaskId);
    }

    [Fact]
    public async Task Requestor_multipart_image_must_match_the_declared_image_contract()
    {
        var image = new InputDefinition("image", CapabilityInputType.Image, true, "Source image", ContentTypes: ["image/png"]);
        var capability = new CapabilityDefinition(CapabilityId.New(), "requestor-image-test", [image], new OutputDefinition(), "requestor-image-contract");
        var unit = new ExecutionUnit(ProviderId, new EnrollmentDefinition(Machine(ResourceTier.Medium, ResourceTier.Medium, 16), [capability]));
        var units = factory.Services.GetRequiredService<IExecutionUnitRepository>();
        var connections = factory.Services.GetRequiredService<ProviderConnectionRegistry>();
        await units.SaveAsync(unit, CancellationToken.None);
        connections.Connect(unit);
        using var client = CreateHttpsClient();
        using var landing = await client.GetAsync("/");
        Assert.True(landing.IsSuccessStatusCode);
        var submission = new SubmitTaskRequestDto(capability.Id.Value, capability.ContractHash, new Dictionary<string, string>(), new MachineSpecifications(ResourceTier.Medium, 16), "image-request-key");
        using var payload = new MultipartFormDataContent();
        payload.Add(new StringContent(JsonSerializer.Serialize(submission)), "submission");
        payload.Add(FilePart(OnePixelPng(), "image/png"), "image", "source.png");

        using var created = await client.PostAsync("/api/tasks/", payload);

        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Grpc_provider_session_authenticates_delivers_assignment_and_issues_upload_authorization()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "grpc-session-test", [], new OutputDefinition(), "grpc-contract");
        var unit = new ExecutionUnit(ProviderId, new EnrollmentDefinition(Machine(ResourceTier.Medium, ResourceTier.Medium, 16), [capability]));
        var units = factory.Services.GetRequiredService<IExecutionUnitRepository>();
        var tasks = factory.Services.GetRequiredService<ITaskRepository>();
        var scheduler = factory.Services.GetRequiredService<SchedulerApplication>();
        await units.SaveAsync(unit, CancellationToken.None);
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string> { ["seed"] = "42" }, null), DateTimeOffset.UtcNow);
        await tasks.SaveAsync(task, CancellationToken.None);

        using var channel = GrpcChannel.ForAddress("https://localhost", new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        var client = new ProviderControl.ProviderControlClient(channel);
        using var call = client.Connect(headers: new Metadata { { "authorization", $"Bearer {ProviderKey}" } });
        await call.RequestStream.WriteAsync(new ProviderMessage { Connect = new ConnectRequest { ProtocolVersion = 1 } });
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(ProviderId.Value.ToString("D"), call.ResponseStream.Current.Connected.ExecutionUnitId);
        using (var diagnostics = CreateHttpsClient())
        {
            Assert.Contains("provider-session", await diagnostics.GetStringAsync("/_netcats/fibers/snapshot"), StringComparison.Ordinal);
        }

        Assert.Equal(1, await scheduler.Evaluate(DateTimeOffset.UtcNow).RunAsync(CancellationToken.None));
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var assignment = call.ResponseStream.Current.Assignment;
        Assert.Equal(task.Id.Value.ToString("D"), assignment.TaskId);
        Assert.Equal("42", assignment.Scalars["seed"]);

        await call.RequestStream.WriteAsync(new ProviderMessage { Accepted = new TaskAccepted { TaskId = assignment.TaskId, AttemptId = assignment.AttemptId, TaskHandle = assignment.TaskHandle } });
        await call.RequestStream.WriteAsync(new ProviderMessage { ResultUpload = new ResultUploadRequest { TaskId = assignment.TaskId, AttemptId = assignment.AttemptId, TaskHandle = assignment.TaskHandle } });
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.False(String.IsNullOrWhiteSpace(call.ResponseStream.Current.ResultUpload.UploadToken));
        var running = await tasks.GetAsync(task.RequestorId, task.Id, CancellationToken.None);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Running, running!.Status);

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task Websocket_provider_session_authenticates_delivers_assignment_and_issues_upload_authorization()
    {
        var capability = new CapabilityDefinition(CapabilityId.New(), "websocket-session-test", [], new OutputDefinition(), "websocket-contract");
        var unit = new ExecutionUnit(ProviderId, new EnrollmentDefinition(Machine(ResourceTier.Medium, ResourceTier.Medium, 16), [capability]));
        var units = factory.Services.GetRequiredService<IExecutionUnitRepository>();
        var tasks = factory.Services.GetRequiredService<ITaskRepository>();
        var scheduler = factory.Services.GetRequiredService<SchedulerApplication>();
        await units.SaveAsync(unit, CancellationToken.None);
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string> { ["seed"] = "24" }, null), DateTimeOffset.UtcNow);
        await tasks.SaveAsync(task, CancellationToken.None);

        using var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("wss://localhost/provider/connect"), CancellationToken.None);
        await SendAsync(socket, new ProviderMessage { Connect = new ConnectRequest { ProtocolVersion = 1, Authorization = ProviderKey } });
        var connected = await ReceiveAsync(socket);
        Assert.Equal(ProviderId.Value.ToString("D"), connected.Connected.ExecutionUnitId);
        using (var diagnostics = CreateHttpsClient())
        {
            Assert.Contains("provider-websocket-session", await diagnostics.GetStringAsync("/_netcats/fibers/snapshot"), StringComparison.Ordinal);
        }

        Assert.Equal(1, await scheduler.Evaluate(DateTimeOffset.UtcNow).RunAsync(CancellationToken.None));
        var assignment = (await ReceiveAsync(socket)).Assignment;
        Assert.Equal(task.Id.Value.ToString("D"), assignment.TaskId);
        Assert.Equal("24", assignment.Scalars["seed"]);

        await SendAsync(socket, new ProviderMessage { Accepted = new TaskAccepted { TaskId = assignment.TaskId, AttemptId = assignment.AttemptId, TaskHandle = assignment.TaskHandle } });
        await SendAsync(socket, new ProviderMessage { ResultUpload = new ResultUploadRequest { TaskId = assignment.TaskId, AttemptId = assignment.AttemptId, TaskHandle = assignment.TaskHandle } });
        var authorization = (await ReceiveAsync(socket)).ResultUpload;
        Assert.False(String.IsNullOrWhiteSpace(authorization.UploadToken));
        var running = await tasks.GetAsync(task.RequestorId, task.Id, CancellationToken.None);
        Assert.Equal(MutualGPU.Domain.TaskStatus.Running, running!.Status);

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None);
    }

    [Fact]
    public async Task Provider_can_stage_all_declared_result_artifacts_and_requestor_receives_descriptors()
    {
        using var client = CreateHttpsClient();
        var capability = new CapabilityDefinition(
            CapabilityId.New(), "artifact-test", [],
            new OutputDefinition(HasThumbnail: true, HasPreview: true, HasMetadata: true, HasLogs: true, PreviewContentTypes: ["image/png"]),
            "artifact-contract");
        var unit = new ExecutionUnit(ProviderId, new EnrollmentDefinition(Machine(ResourceTier.Medium, ResourceTier.Medium, 16), [capability]));
        var requestor = RequestorId.New();
        var task = new TaskRequest(TaskId.New(), requestor, capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var attempt = task.Assign(AttemptId.New(), unit.Id, "artifact-test-handle", DateTimeOffset.UtcNow);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UtcNow);
        var tasks = factory.Services.GetRequiredService<ITaskRepository>();
        var connections = factory.Services.GetRequiredService<ProviderConnectionRegistry>();
        await tasks.SaveAsync(task, CancellationToken.None);
        connections.Connect(unit);
        connections.Track(unit.Id, task, attempt);

        using var tokenRequest = ProviderRequest(HttpMethod.Post, $"/provider/tasks/{task.Id.Value:D}/attempts/{attempt.Id.Value:D}/upload-token", attempt.Handle);
        using var tokenResponse = await client.SendAsync(tokenRequest, CancellationToken.None);
        Assert.True(tokenResponse.IsSuccessStatusCode);
        using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var token = tokenDocument.RootElement.GetProperty("token").GetString();
        Assert.False(String.IsNullOrWhiteSpace(token));

        using var upload = new MultipartFormDataContent();
        var zip = ValidZip();
        upload.Add(FilePart(zip, "application/zip"), "result", "result.zip");
        upload.Add(new StringContent("{\"frames\":12}", Encoding.UTF8, "application/json"), "metadata");
        upload.Add(FilePart(OnePixelPng(), "image/png"), "thumbnail", "thumbnail.png");
        upload.Add(FilePart(OnePixelPng(), "image/png"), "preview", "preview.png");
        upload.Add(FilePart(Encoding.UTF8.GetBytes("provider log"), "text/plain"), "logs", "logs.txt");
        using var uploadRequest = ProviderRequest(HttpMethod.Post, $"/provider/tasks/{task.Id.Value:D}/attempts/{attempt.Id.Value:D}/result", attempt.Handle);
        uploadRequest.Content = upload;
        uploadRequest.Headers.Add("X-MutualGPU-Upload-Token", token);
        uploadRequest.Headers.Add("X-MutualGPU-Sha256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)).ToLowerInvariant());
        using var uploadResponse = await client.SendAsync(uploadRequest, CancellationToken.None);
        var uploadBody = await uploadResponse.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.True(uploadResponse.IsSuccessStatusCode, uploadBody);
        using var uploadDocument = JsonDocument.Parse(uploadBody);
        var receipt = uploadDocument.RootElement.GetProperty("receipt").GetString();
        Assert.False(String.IsNullOrWhiteSpace(receipt));

        using var completeRequest = ProviderRequest(HttpMethod.Post, $"/provider/tasks/{task.Id.Value:D}/attempts/{attempt.Id.Value:D}/complete/{receipt}", attempt.Handle);
        using var completeResponse = await client.SendAsync(completeRequest, CancellationToken.None);
        Assert.True(completeResponse.IsSuccessStatusCode);

        using var resultRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/tasks/{task.Id.Value:D}/result");
        resultRequest.Headers.Add("Cookie", $"{RequestorIdentity.CookieName}={requestor.Value:D}");
        using var requestorClient = CreateHttpsClient(handleCookies: false);
        using var resultResponse = await requestorClient.SendAsync(resultRequest, CancellationToken.None);
        Assert.True(resultResponse.IsSuccessStatusCode);
        using var resultDocument = JsonDocument.Parse(await resultResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var artifacts = resultDocument.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.Equal(["result", "metadata", "thumbnail", "preview", "logs"], artifacts.Select(static artifact => artifact.GetProperty("name").GetString()));
        Assert.All(artifacts, static artifact => Assert.True(artifact.GetProperty("length").GetInt64() > 0));
    }

    [Fact]
    public async Task Provider_cannot_request_a_result_upload_token_before_accepting_the_assignment()
    {
        using var client = CreateHttpsClient();
        var capability = new CapabilityDefinition(CapabilityId.New(), "upload-before-accept-test", [], new OutputDefinition(), "contract");
        var unit = new ExecutionUnit(ProviderId, new EnrollmentDefinition(Machine(ResourceTier.Medium, ResourceTier.Medium, 16), [capability]));
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var attempt = task.Assign(AttemptId.New(), unit.Id, "upload-before-accept-handle", DateTimeOffset.UtcNow);
        var tasks = factory.Services.GetRequiredService<ITaskRepository>();
        var connections = factory.Services.GetRequiredService<ProviderConnectionRegistry>();
        await tasks.SaveAsync(task, CancellationToken.None);
        connections.Connect(unit);
        connections.Track(unit.Id, task, attempt);

        using var request = ProviderRequest(HttpMethod.Post, $"/provider/tasks/{task.Id.Value:D}/attempts/{attempt.Id.Value:D}/upload-token", attempt.Handle);
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Revoked_assignment_cannot_publish_with_a_previously_issued_upload_token()
    {
        using var client = CreateHttpsClient();
        var capability = new CapabilityDefinition(CapabilityId.New(), "revoked-upload-test", [], new OutputDefinition(), "contract");
        var unit = new ExecutionUnit(ProviderId, new EnrollmentDefinition(Machine(ResourceTier.Medium, ResourceTier.Medium, 16), [capability]));
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var attempt = task.Assign(AttemptId.New(), unit.Id, "revoked-upload-handle", DateTimeOffset.UtcNow);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UtcNow);
        var tasks = factory.Services.GetRequiredService<ITaskRepository>();
        var connections = factory.Services.GetRequiredService<ProviderConnectionRegistry>();
        await tasks.SaveAsync(task, CancellationToken.None);
        connections.Connect(unit);
        connections.Track(unit.Id, task, attempt);

        using var tokenRequest = ProviderRequest(HttpMethod.Post, $"/provider/tasks/{task.Id.Value:D}/attempts/{attempt.Id.Value:D}/upload-token", attempt.Handle);
        using var tokenResponse = await client.SendAsync(tokenRequest, CancellationToken.None);
        using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var token = tokenDocument.RootElement.GetProperty("token").GetString();
        connections.Remove(unit.Id, task.Id, attempt.Id);
        var zip = ValidZip();
        using var upload = new MultipartFormDataContent();
        upload.Add(FilePart(zip, "application/zip"), "result", "result.zip");
        using var request = ProviderRequest(HttpMethod.Post, $"/provider/tasks/{task.Id.Value:D}/attempts/{attempt.Id.Value:D}/result", attempt.Handle);
        request.Content = upload;
        request.Headers.Add("X-MutualGPU-Upload-Token", token);
        request.Headers.Add("X-MutualGPU-Sha256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)).ToLowerInvariant());

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Provider_result_upload_rejects_a_truncated_zip_header()
    {
        using var client = CreateHttpsClient();
        var capability = new CapabilityDefinition(CapabilityId.New(), "zip-validation-test", [], new OutputDefinition(), "zip-contract");
        var unit = new ExecutionUnit(ProviderId, new EnrollmentDefinition(Machine(ResourceTier.Medium, ResourceTier.Medium, 16), [capability]));
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var attempt = task.Assign(AttemptId.New(), unit.Id, "zip-validation-handle", DateTimeOffset.UtcNow);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UtcNow);
        var tasks = factory.Services.GetRequiredService<ITaskRepository>();
        var connections = factory.Services.GetRequiredService<ProviderConnectionRegistry>();
        await tasks.SaveAsync(task, CancellationToken.None);
        connections.Connect(unit);
        connections.Track(unit.Id, task, attempt);

        using var tokenRequest = ProviderRequest(HttpMethod.Post, $"/provider/tasks/{task.Id.Value:D}/attempts/{attempt.Id.Value:D}/upload-token", attempt.Handle);
        using var tokenResponse = await client.SendAsync(tokenRequest, CancellationToken.None);
        using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var token = tokenDocument.RootElement.GetProperty("token").GetString();
        var fakeZip = new byte[] { 0x50, 0x4b, 0x03, 0x04 };
        using var upload = new MultipartFormDataContent();
        upload.Add(FilePart(fakeZip, "application/zip"), "result", "result.zip");
        using var request = ProviderRequest(HttpMethod.Post, $"/provider/tasks/{task.Id.Value:D}/attempts/{attempt.Id.Value:D}/result", attempt.Handle);
        request.Content = upload;
        request.Headers.Add("X-MutualGPU-Upload-Token", token);
        request.Headers.Add("X-MutualGPU-Sha256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fakeZip)).ToLowerInvariant());

        using var response = await client.SendAsync(request, CancellationToken.None);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("result_zip_invalid", body.RootElement.GetProperty("code").GetString());
        var reloaded = await tasks.GetAsync(task.RequestorId, task.Id, CancellationToken.None);
        Assert.True(reloaded!.Status is MutualGPU.Domain.TaskStatus.Queued or MutualGPU.Domain.TaskStatus.Assigned);
        Assert.Equal(AttemptState.Failed, Assert.Single(reloaded.Attempts, candidate => candidate.Id == attempt.Id).State);
        Assert.False(connections.TryGet(unit.Id, task.Id, attempt.Id, attempt.Handle, out _));
    }

    [Fact]
    public async Task Provider_result_upload_checksum_failure_requeues_the_accepted_attempt()
    {
        using var client = CreateHttpsClient();
        var capability = new CapabilityDefinition(CapabilityId.New(), "checksum-validation-test", [], new OutputDefinition(), "checksum-contract");
        var unit = new ExecutionUnit(ProviderId, new EnrollmentDefinition(Machine(ResourceTier.Medium, ResourceTier.Medium, 16), [capability]));
        var task = new TaskRequest(TaskId.New(), RequestorId.New(), capability, ResourceTier.Automatic, new TaskParameters(new Dictionary<string, string>(), null), DateTimeOffset.UtcNow);
        var attempt = task.Assign(AttemptId.New(), unit.Id, "checksum-validation-handle", DateTimeOffset.UtcNow);
        task.Accept(attempt.Id, attempt.Handle, DateTimeOffset.UtcNow);
        var tasks = factory.Services.GetRequiredService<ITaskRepository>();
        var connections = factory.Services.GetRequiredService<ProviderConnectionRegistry>();
        await tasks.SaveAsync(task, CancellationToken.None);
        connections.Connect(unit);
        connections.Track(unit.Id, task, attempt);

        using var tokenRequest = ProviderRequest(HttpMethod.Post, $"/provider/tasks/{task.Id.Value:D}/attempts/{attempt.Id.Value:D}/upload-token", attempt.Handle);
        using var tokenResponse = await client.SendAsync(tokenRequest, CancellationToken.None);
        using var tokenDocument = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(CancellationToken.None));
        var token = tokenDocument.RootElement.GetProperty("token").GetString();
        var zip = ValidZip();
        using var upload = new MultipartFormDataContent();
        upload.Add(FilePart(zip, "application/zip"), "result", "result.zip");
        using var request = ProviderRequest(HttpMethod.Post, $"/provider/tasks/{task.Id.Value:D}/attempts/{attempt.Id.Value:D}/result", attempt.Handle);
        request.Content = upload;
        request.Headers.Add("X-MutualGPU-Upload-Token", token);
        request.Headers.Add("X-MutualGPU-Sha256", new string('0', 64));

        using var response = await client.SendAsync(request, CancellationToken.None);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("result_checksum_invalid", body.RootElement.GetProperty("code").GetString());
        var reloaded = await tasks.GetAsync(task.RequestorId, task.Id, CancellationToken.None);
        Assert.True(reloaded!.Status is MutualGPU.Domain.TaskStatus.Queued or MutualGPU.Domain.TaskStatus.Assigned);
        Assert.Equal(AttemptState.Failed, Assert.Single(reloaded.Attempts, candidate => candidate.Id == attempt.Id).State);
        Assert.False(connections.TryGet(unit.Id, task.Id, attempt.Id, attempt.Handle, out _));
    }

    private static HttpRequestMessage ProviderRequest(HttpMethod method, string path, string handle)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ProviderKey);
        request.Headers.Add("X-MutualGPU-Task-Handle", handle);
        return request;
    }

    private HttpClient CreateHttpsClient(bool handleCookies = true) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = handleCookies,
    });

    private static ByteArrayContent FilePart(byte[] bytes, string contentType)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return part;
    }

    private static byte[] OnePixelPng()
    {
        var png = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0);
        png[19] = png[23] = 1;
        return png;
    }

    private static byte[] ValidZip()
    {
        using var content = new MemoryStream();
        using (var archive = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("result.txt").Open());
            writer.Write("mutualgpu");
        }
        return content.ToArray();
    }

    private static MachineProfile Machine(ResourceTier tier, ResourceTier computeTier, int memoryGiB) =>
        new(tier, new MachineSpecifications(computeTier, memoryGiB));

    private static Task SendAsync(WebSocket socket, ProviderMessage message) => socket.SendAsync(message.ToByteArray(), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

    private static async Task<ServerMessage> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.True(result.EndOfMessage);
        return ServerMessage.Parser.ParseFrom(buffer, 0, result.Count);
    }
}
