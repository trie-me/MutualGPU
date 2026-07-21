namespace MutualGPU.Domain;

public sealed class ExecutionUnit
{
    public ExecutionUnit(ExecutionUnitId id, EnrollmentDefinition enrollment)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        enrollment.Validate();
        Id = id;
        CurrentEnrollment = enrollment;
        Version = EnrollmentVersion.Initial;
    }

    public ExecutionUnitId Id { get; }

    public EnrollmentVersion Version { get; private set; }

    public EnrollmentDefinition CurrentEnrollment { get; private set; }

    public void ReplaceEnrollment(EnrollmentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        CurrentEnrollment = definition;
        Version = Version.Next();
    }

    public ExecutionUnitSnapshot ToSnapshot() => new(Id, Version, CurrentEnrollment);

    public static ExecutionUnit Hydrate(ExecutionUnitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var unit = new ExecutionUnit(snapshot.Id, snapshot.Enrollment)
        {
            Version = snapshot.Version,
        };
        return unit;
    }
}

public sealed record ExecutionUnitSnapshot(ExecutionUnitId Id, EnrollmentVersion Version, EnrollmentDefinition Enrollment);
