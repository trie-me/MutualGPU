using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MutualGPU.Application;
using MutualGPU.Domain;
using Npgsql;
using NpgsqlTypes;

namespace MutualGPU.Infrastructure;

internal static class PostgresPersistence
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    internal static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    internal static T Deserialize<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, JsonOptions)
        ?? throw new InvalidOperationException($"PostgreSQL JSON could not be hydrated as {typeof(T).Name}.");

    internal static NpgsqlParameter JsonParameter(string name, object value) =>
        new(name, NpgsqlDbType.Jsonb) { Value = Serialize(value) };

    internal static void AddNullableText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Text).Value = (object?)value ?? DBNull.Value;

    internal static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(name, NpgsqlDbType.Uuid).Value = value is null ? DBNull.Value : value.Value;

    internal static void AddNullableTimestamp(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value =
            value is null ? DBNull.Value : Utc(value.Value);

    internal static DateTime Utc(DateTimeOffset value) => value.UtcDateTime;

    internal static DateTimeOffset Timestamp(NpgsqlDataReader reader, int ordinal) =>
        new(reader.GetFieldValue<DateTime>(ordinal), TimeSpan.Zero);

    internal static DateTimeOffset? NullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Timestamp(reader, ordinal);

    internal static string NormalizeCapabilityName(string name) =>
        name.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    internal static string NormalizeOrigin(string origin) =>
        origin.Trim().ToLowerInvariant();

    internal static string SubmissionFingerprint(TaskRequest task)
    {
        var parameters = task.Parameters;
        var canonical = new
        {
            capabilityId = task.Capability.Id.Value.ToString("D", CultureInfo.InvariantCulture),
            task.Capability.ContractHash,
            computeTier = (int)task.Resources.ComputeTier,
            task.Resources.MemoryGiB,
            scalars = parameters.Scalars
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new[] { pair.Key, pair.Value })
                .ToArray(),
            image = parameters.Image is null
                ? null
                : new
                {
                    parameters.ImageContentType,
                    parameters.ImageLength,
                    sha256 = parameters.ImageSha256?.ToLowerInvariant(),
                },
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(canonical)))).ToLowerInvariant();
    }

    internal static string EventType(TaskRequestSnapshot? original, TaskRequest task)
    {
        if (original is null) return "submitted";
        var originalAttempts = original.Attempts.ToDictionary(static attempt => attempt.Id);
        var changedAttempt = task.Attempts.LastOrDefault(attempt =>
            !originalAttempts.TryGetValue(attempt.Id, out var old) || old.State != attempt.State);
        return changedAttempt is null
            ? task.Status.ToString().ToLowerInvariant()
            : $"attempt_{changedAttempt.State.ToString().ToLowerInvariant()}";
    }

    internal static AttemptId? ChangedAttempt(TaskRequestSnapshot? original, TaskRequest task)
    {
        if (original is null) return null;
        var originalAttempts = original.Attempts.ToDictionary(static attempt => attempt.Id);
        return task.Attempts.LastOrDefault(attempt =>
            !originalAttempts.TryGetValue(attempt.Id, out var old) || old.State != attempt.State)?.Id;
    }
}

internal sealed class OperationUsageGuard
{
    private int active;
    private int completed;

    public async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        AssertOpen();
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
        {
            throw new InvalidOperationException("Concurrent repository use inside one operation is not supported.");
        }

        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref active, 0);
        }
    }

    public void Run(Action operation)
    {
        AssertOpen();
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
        {
            throw new InvalidOperationException("Concurrent repository use inside one operation is not supported.");
        }

        try
        {
            operation();
        }
        finally
        {
            Volatile.Write(ref active, 0);
        }
    }

    public void Complete() => Volatile.Write(ref completed, 1);

    private void AssertOpen()
    {
        if (Volatile.Read(ref completed) != 0)
        {
            throw new InvalidOperationException("The operation context has already completed.");
        }
    }
}
