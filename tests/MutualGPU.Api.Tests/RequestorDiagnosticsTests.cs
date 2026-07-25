using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MutualGPU.Domain;

namespace MutualGPU.Api.Tests;

public sealed class RequestorDiagnosticsTests
{
    [Fact]
    public void Ip_hash_is_keyed_stable_normalized_and_does_not_contain_the_address()
    {
        var first = new RequestorDiagnostics("diagnostic-test-key", NullLogger<RequestorDiagnostics>.Instance);
        var second = new RequestorDiagnostics("diagnostic-test-key", NullLogger<RequestorDiagnostics>.Instance);
        var differentKey = new RequestorDiagnostics("different-diagnostic-test-key", NullLogger<RequestorDiagnostics>.Instance);
        var address = IPAddress.Parse("203.0.113.42");

        var hash = first.HashIpAddress(address);

        Assert.Equal(hash, second.HashIpAddress(address));
        Assert.Equal(hash, first.HashIpAddress(IPAddress.Parse("::ffff:203.0.113.42")));
        Assert.NotEqual(hash, first.HashIpAddress(IPAddress.Parse("203.0.113.43")));
        Assert.NotEqual(hash, differentKey.HashIpAddress(address));
        Assert.Matches("^[0-9a-f]{32}$", hash);
        Assert.DoesNotContain("203", hash, StringComparison.Ordinal);
        Assert.Equal("unknown", first.HashIpAddress(null));
        Assert.Equal("203.0.*.*", RequestorDiagnostics.ClassABSegment(address));
        Assert.Equal("203.0.*.*", RequestorDiagnostics.ClassABSegment(IPAddress.Parse("::ffff:203.0.113.42")));
        Assert.Equal("2001:0db8:*", RequestorDiagnostics.ClassABSegment(IPAddress.Parse("2001:db8:1234:5678::1")));
        Assert.Equal("unknown", RequestorDiagnostics.ClassABSegment(null));
    }

    [Fact]
    public void Task_operation_logs_correlation_fields_without_the_raw_ip()
    {
        var logger = new RecordingLogger<RequestorDiagnostics>();
        var diagnostics = new RequestorDiagnostics("diagnostic-test-key", logger);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");
        var requestorId = RequestorId.New();
        var taskId = TaskId.New();

        diagnostics.TaskOperation(context, requestorId, taskId, "submitted");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("submitted", entry.Properties["Operation"]);
        Assert.Equal(requestorId.Value, entry.Properties["RequestorId"]);
        Assert.Equal(taskId.Value, entry.Properties["TaskId"]);
        Assert.Matches("^[0-9a-f]{32}$", Assert.IsType<string>(entry.Properties["RequestorIpHash"]));
        Assert.Equal("203.0.*.*", entry.Properties["RequestorIpClassAB"]);
        Assert.DoesNotContain("203.0.113.42", entry.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.Where(static pair => pair.Key != "{OriginalFormat}").ToDictionary(static pair => pair.Key, static pair => pair.Value)
                : [];
            Entries.Add(new Entry(logLevel, formatter(state, exception), properties));
        }
    }

    private sealed record Entry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties);
}
