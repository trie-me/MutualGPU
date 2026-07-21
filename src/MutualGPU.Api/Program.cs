using NetCats.AspNetCore;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using MutualGPU.Api;
using MutualGPU.Application;
using MutualGPU.Domain;
using MutualGPU.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddGrpc();
// Result parts have tighter per-part validation in ProviderResultEndpoints. These limits
// bound the parser before it buffers a provider multipart request.
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 64L * 1024 * 1024;
    options.ValueLengthLimit = 64 * 1024;
    options.MultipartHeadersLengthLimit = 16 * 1024;
});
var providerCorsOrigins = builder.Configuration.GetSection("MutualGPU:ProviderCorsOrigins").Get<string[]>() ?? [];
var trustForwardedProto = builder.Configuration.GetValue("MutualGPU:TrustForwardedProto", false);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (trustForwardedProto)
    {
        // Set only when the host is reachable exclusively through a trusted TLS-terminating proxy.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
});
builder.Services.AddCors(options => options.AddPolicy("mutualgpu-provider", policy =>
{
    if (providerCorsOrigins.Length > 0)
    {
        policy.WithOrigins(providerCorsOrigins)
            .WithMethods("POST")
            .WithHeaders("Authorization", "Content-Type", "X-MutualGPU-Task-Handle", "X-MutualGPU-Upload-Token", "X-MutualGPU-Sha256")
            .AllowCredentials();
    }
}));

var providerCredentials = builder.Configuration.GetSection("MutualGPU:Providers").Get<ProviderCredential[]>() ?? [];
var providerKeys = providerCredentials
    .Where(static credential => Guid.TryParse(credential.ExecutionUnitId, out _) && !String.IsNullOrWhiteSpace(credential.PresharedKey))
    .ToDictionary(static credential => new ExecutionUnitId(Guid.Parse(credential.ExecutionUnitId)), static credential => credential.PresharedKey);
var objectKeys = new MutualGpuObjectKeys();
var providerKeyS3 = builder.Configuration.GetSection("MutualGPU:ProviderKeyS3").Get<AwsS3ProviderKeyRegistryOptions>();
var s3 = builder.Configuration.GetSection("MutualGPU:S3").Get<AwsS3ObjectStoreOptions>();
builder.Services.AddSingleton(objectKeys);
builder.Services.AddSingleton<RepositoryLockRegistry>();
builder.Services.AddSingleton<IEnrollmentGate>(static services => services.GetRequiredService<RepositoryLockRegistry>());
if (s3 is not null)
{
    s3.Validate();
    builder.Services.AddSingleton(s3);
    builder.Services.AddSingleton<AwsS3ObjectStore>();
    builder.Services.AddSingleton<IObjectStore>(static services => services.GetRequiredService<AwsS3ObjectStore>());
    builder.Services.AddSingleton<IObjectStoreHealth>(static services => services.GetRequiredService<AwsS3ObjectStore>());
}
else if (builder.Configuration.GetSection("MutualGPU:Backblaze").Exists())
{
    var backblaze = builder.Configuration.GetRequiredSection("MutualGPU:Backblaze").Get<BackblazeS3Options>()
        ?? throw new InvalidOperationException("MutualGPU Backblaze configuration is invalid.");
    builder.Services.AddMutualGpuBackblazeObjectStore(backblaze);
}
else
{
    builder.Services.AddSingleton<InMemoryObjectStore>();
    builder.Services.AddSingleton<IObjectStore>(static services => services.GetRequiredService<InMemoryObjectStore>());
    builder.Services.AddSingleton<IObjectStoreHealth>(static services => services.GetRequiredService<InMemoryObjectStore>());
}

if (providerKeyS3 is not null)
{
    providerKeyS3.Validate();
    builder.Services.AddSingleton(providerKeyS3);
    builder.Services.AddSingleton<AwsS3ProviderKeyRegistry>();
    builder.Services.AddSingleton<IExecutionUnitKeyRegistry>(static services => services.GetRequiredService<AwsS3ProviderKeyRegistry>());
    builder.Services.AddSingleton<IExecutionUnitKeyResolver>(static services => services.GetRequiredService<AwsS3ProviderKeyRegistry>());
    builder.Services.AddSingleton<IExecutionUnitAuthenticator>(static services => services.GetRequiredService<AwsS3ProviderKeyRegistry>());
}
else if (builder.Configuration.GetSection("MutualGPU:Backblaze").Exists())
{
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException("MutualGPU production requires MutualGPU:ProviderKeyS3 configuration.");
    }

    builder.Services.AddSingleton<ObjectStoreProviderKeyRegistry>();
    builder.Services.AddSingleton<IExecutionUnitKeyRegistry>(static services => services.GetRequiredService<ObjectStoreProviderKeyRegistry>());
    builder.Services.AddSingleton<IExecutionUnitKeyResolver>(static services => services.GetRequiredService<ObjectStoreProviderKeyRegistry>());
    builder.Services.AddSingleton<IExecutionUnitAuthenticator>(static services => services.GetRequiredService<ObjectStoreProviderKeyRegistry>());
}
else
{
    builder.Services.AddSingleton(_ => new ConfiguredPresharedKeyRegistry(providerKeys, objectKeys));
    builder.Services.AddSingleton<IExecutionUnitKeyRegistry>(static services => services.GetRequiredService<ConfiguredPresharedKeyRegistry>());
    builder.Services.AddSingleton<IExecutionUnitKeyResolver>(static services => services.GetRequiredService<ConfiguredPresharedKeyRegistry>());
    builder.Services.AddSingleton<IExecutionUnitAuthenticator>(static services => services.GetRequiredService<ConfiguredPresharedKeyRegistry>());
}
builder.Services.AddSingleton<ProviderKeyIssuer>();
builder.Services.AddSingleton<ObjectStoreExecutionUnitRepository>();
builder.Services.AddSingleton<IExecutionUnitRepository>(static services => services.GetRequiredService<ObjectStoreExecutionUnitRepository>());
builder.Services.AddSingleton<ICapabilityReader>(static services => services.GetRequiredService<ObjectStoreExecutionUnitRepository>());
builder.Services.AddSingleton<IEnrollmentStartupRecovery>(static services => services.GetRequiredService<ObjectStoreExecutionUnitRepository>());
builder.Services.AddSingleton<ObjectStoreTaskRepository>();
builder.Services.AddSingleton<ITaskRepository>(static services => services.GetRequiredService<ObjectStoreTaskRepository>());
builder.Services.AddSingleton<ITaskSummaryReader>(static services => services.GetRequiredService<ObjectStoreTaskRepository>());
builder.Services.AddSingleton<IQueuedTaskReader>(static services => services.GetRequiredService<ObjectStoreTaskRepository>());
builder.Services.AddSingleton<IStartupRecovery>(static services => services.GetRequiredService<ObjectStoreTaskRepository>());
builder.Services.AddSingleton<ProviderConnectionRegistry>();
builder.Services.AddSingleton<IProviderPresence>(static services => services.GetRequiredService<ProviderConnectionRegistry>());
builder.Services.AddSingleton<IProviderAssignments>(static services => services.GetRequiredService<ProviderConnectionRegistry>());
builder.Services.AddSingleton<IProviderProgress>(static services => services.GetRequiredService<ProviderConnectionRegistry>());
builder.Services.AddSingleton<ResultUploadAuthorizations>();
builder.Services.AddSingleton<IResultUploadAuthorizations>(static services => services.GetRequiredService<ResultUploadAuthorizations>());
builder.Services.AddSingleton<IStagedResults>(static services => services.GetRequiredService<ResultUploadAuthorizations>());
builder.Services.AddSingleton<SchedulerSignal>();
builder.Services.AddSingleton<IApplicationEventSink>(static services => services.GetRequiredService<SchedulerSignal>());
builder.Services.AddSingleton<MutualGpuTelemetry>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<EnrollmentApplication>();
builder.Services.AddSingleton<CapabilityCatalogueApplication>();
builder.Services.AddSingleton<TaskSubmissionApplication>();
builder.Services.AddSingleton<ProviderSessionApplication>();
builder.Services.AddSingleton<SchedulerApplication>();
builder.Services.AddSingleton<MutualGpuFiberOwner>();
builder.Services.AddSingleton<TaskAttemptFiberTracker>();
builder.Services.AddHostedService(static services => services.GetRequiredService<MutualGpuFiberOwner>());
builder.Services.AddHostedService<SchedulerHostedService>();
builder.Services.AddSingleton<DisconnectRecoveryService>();
builder.Services.AddHostedService(static services => services.GetRequiredService<DisconnectRecoveryService>());
builder.Services.AddSingleton<StartupProjectionState>();
builder.Services.AddHostedService<StartupProjectionHostedService>();

// Diagnostics are not part of the normal request path. Enable explicitly with
// NetCats__FiberDiagnostics__Enabled=true; no observer or projection is created otherwise.
var demoForestSimulationsEnabled = false;
if (Boolean.TryParse(builder.Configuration["NetCats:FiberDiagnostics:Enabled"], out var diagnosticsEnabled) && diagnosticsEnabled)
{
    builder.Services.AddNetCatsFiberDiagnostics(options =>
    {
        options.EnableInProduction = builder.Configuration.GetValue("NetCats:FiberDiagnostics:EnableInProduction", false);
        options.CompletedRetention = TimeSpan.FromSeconds(3);
    });
    if (builder.Configuration.GetValue("MutualGPU:Demo:SimulateForest", false))
    {
        demoForestSimulationsEnabled = true;
        builder.Services.AddSingleton<DemoSimulationRegistry>();
        builder.Services.AddSingleton<DemoForestSimulationHostedService>();
        builder.Services.AddHostedService(static services => services.GetRequiredService<DemoForestSimulationHostedService>());
    }
}

var app = builder.Build();
app.UseExceptionHandler();
app.UseForwardedHeaders();
app.UseHsts();
app.Use(async (context, next) =>
{
    // The task security group admits these plaintext Kestrel ports only from the ALB.
    // Public callers terminate TLS at that ingress and arrive with X-Forwarded-Proto.
    var isPrivateLoadBalancerHop = context.Connection.LocalPort is 8080 or 8081;
    if (!context.Request.IsHttps && !isPrivateLoadBalancerHop)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("HTTPS is required.", context.RequestAborted);
        return;
    }
    await next(context);
});
app.UseCors("mutualgpu-provider");
app.UseWebSockets();
app.Use(async (context, next) =>
{
    if (!Guid.TryParse(context.Request.Cookies[RequestorIdentity.CookieName], out _))
    {
        context.Response.Cookies.Append(RequestorIdentity.CookieName, Guid.CreateVersion7().ToString("D"), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddYears(1),
        });
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi();
app.MapGet("/health/live", () => TypedResults.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", (StartupProjectionState state) => state.IsReady
    ? (IResult)TypedResults.Ok(new { status = "ready" })
    : TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable));
app.MapGrpcService<ProviderControlService>();
app.MapPost("/provider/enroll", ProviderWebSocketEndpoints.Enroll);
app.Map("/provider/connect", ProviderWebSocketEndpoints.Connect);
app.MapPost("/api/webgpu-enrollments", WebGpuEnrollmentEndpoints.Create);
app.MapPost("/provider/tasks/{taskId:guid}/attempts/{attemptId:guid}/upload-token", ProviderResultEndpoints.IssueToken);
app.MapPost("/provider/tasks/{taskId:guid}/attempts/{attemptId:guid}/result", ProviderResultEndpoints.Upload);
app.MapPost("/provider/tasks/{taskId:guid}/attempts/{attemptId:guid}/complete/{receipt}", ProviderResultEndpoints.Complete);
var capabilities = app.MapGroup("/api/capabilities");
capabilities.MapGet("/", MutualGpuEndpoints.ListCapabilities);
var tasks = app.MapGroup("/api/tasks");
tasks.MapPost("/", MutualGpuEndpoints.SubmitTask);
tasks.MapGet("/", MutualGpuEndpoints.ListTasks);
tasks.MapGet("/{taskId:guid}", MutualGpuEndpoints.GetTask);
tasks.MapPost("/{taskId:guid}/reevaluate", MutualGpuEndpoints.ReevaluateTask);
tasks.MapGet("/{taskId:guid}/result", MutualGpuEndpoints.GetTaskResult);

if (diagnosticsEnabled)
{
    app.MapNetCatsFiberDiagnostics("/_netcats/fibers");
}
if (demoForestSimulationsEnabled)
{
    app.MapGet("/_netcats/fiber-simulations", (DemoSimulationRegistry simulations) => Results.Json(simulations.Snapshot()));
    app.MapGet("/_netcats/fiber-simulations/scenarios", (DemoForestSimulationHostedService simulations) => Results.Json(simulations.List()));
    app.MapPost("/_netcats/fiber-simulations/scenarios/{id}", (string id, DemoForestSimulationHostedService simulations) =>
        simulations.Run(id) ? Results.Accepted() : Results.Conflict());
}

app.Run();

public partial class Program;

public sealed record ProviderCredential(string ExecutionUnitId, string PresharedKey);
