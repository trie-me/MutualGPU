using System.Diagnostics;
using System.Diagnostics.Metrics;
using MutualGPU.Application;

namespace MutualGPU.Api;

/// <summary>Low-cardinality service telemetry. Never attach identities, handles, tokens, or object keys as tags.</summary>
public sealed class MutualGpuTelemetry : IDisposable
{
    public const string MeterName = "NetCats.Examples.MutualGPU";
    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> submitted;
    private readonly Counter<long> assigned;
    private readonly Histogram<long> uploadedBytes;
    private readonly Counter<long> providerProgressReceived;
    private readonly Counter<long> providerProgressAccepted;
    private readonly Counter<long> providerProgressDropped;

    public MutualGpuTelemetry()
    {
        submitted = meter.CreateCounter<long>("mutualgpu.tasks.submitted");
        assigned = meter.CreateCounter<long>("mutualgpu.attempts.assigned");
        uploadedBytes = meter.CreateHistogram<long>("mutualgpu.upload.bytes", unit: "By");
        providerProgressReceived = meter.CreateCounter<long>("mutualgpu.provider.progress.received");
        providerProgressAccepted = meter.CreateCounter<long>("mutualgpu.provider.progress.accepted");
        providerProgressDropped = meter.CreateCounter<long>("mutualgpu.provider.progress.dropped");
    }

    public ActivitySource Activities { get; } = new(MeterName);

    public void TaskSubmitted() => submitted.Add(1);

    public void AttemptsAssigned(int count)
    {
        if (count > 0) assigned.Add(count);
    }

    public void UploadCompleted(long bytes)
    {
        if (bytes > 0) uploadedBytes.Record(bytes);
    }

    public void ProviderProgress(string transport, ProviderProgressDisposition disposition)
    {
        var transportTag = new KeyValuePair<string, object?>("transport", transport);
        providerProgressReceived.Add(1, transportTag);
        if (disposition is ProviderProgressDisposition.Accepted)
        {
            providerProgressAccepted.Add(1, transportTag);
        }
        else if (disposition is ProviderProgressDisposition.DroppedStaleSequence or ProviderProgressDisposition.DroppedSuperseded)
        {
            providerProgressDropped.Add(1, transportTag, new KeyValuePair<string, object?>("reason", disposition.ToString()));
        }
    }

    public void Dispose()
    {
        Activities.Dispose();
        meter.Dispose();
    }
}
