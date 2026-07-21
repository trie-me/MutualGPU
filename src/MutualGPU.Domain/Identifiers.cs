namespace MutualGPU.Domain;

public readonly record struct RequestorId(Guid Value)
{
    public static RequestorId New() => new(Guid.CreateVersion7());
}

public readonly record struct ExecutionUnitId(Guid Value)
{
    public static ExecutionUnitId New() => new(Guid.CreateVersion7());
}

public readonly record struct CapabilityId(Guid Value)
{
    public static CapabilityId New() => new(Guid.CreateVersion7());
}

public readonly record struct TaskId(Guid Value)
{
    public static TaskId New() => new(Guid.CreateVersion7());
}

public readonly record struct AttemptId(Guid Value)
{
    public static AttemptId New() => new(Guid.CreateVersion7());
}

public readonly record struct ArtifactId(Guid Value)
{
    public static ArtifactId New() => new(Guid.CreateVersion7());
}

public readonly record struct EnrollmentEventId(Guid Value)
{
    public static EnrollmentEventId New() => new(Guid.CreateVersion7());
}

public readonly record struct EnrollmentVersion(int Value)
{
    public static EnrollmentVersion Initial => new(1);

    public EnrollmentVersion Next() => new(checked(Value + 1));
}

public sealed class DomainRuleViolation(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
