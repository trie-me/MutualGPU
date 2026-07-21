using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MutualGPU.Api;

/// <summary>Low-cardinality service telemetry. Never attach identities, handles, tokens, or object keys as tags.</summary>
public sealed class MutualGpuTelemetry : IDisposable
{
    public const string MeterName = "NetCats.Examples.MutualGPU";
    private readonly Meter meter = new(MeterName);
    private readonly Counter<long> submitted;
    private readonly Counter<long> assigned;
    private readonly Histogram<long> uploadedBytes;

    public MutualGpuTelemetry()
    {
        submitted = meter.CreateCounter<long>("mutualgpu.tasks.submitted");
        assigned = meter.CreateCounter<long>("mutualgpu.attempts.assigned");
        uploadedBytes = meter.CreateHistogram<long>("mutualgpu.upload.bytes", unit: "By");
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

    public void Dispose()
    {
        Activities.Dispose();
        meter.Dispose();
    }
}
