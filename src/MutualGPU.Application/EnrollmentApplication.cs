using MutualGPU.Domain;
using NetCats.Core;

namespace MutualGPU.Application;

public sealed record EnrollCommand(ExecutionUnitId ExecutionUnitId, MachineProfile Machine, IReadOnlyList<CapabilityDefinition> Capabilities);

public abstract record EnrollResult
{
    public sealed record Enrolled(ExecutionUnit Unit) : EnrollResult;

    public sealed record Conflict(IReadOnlyList<CapabilityContractConflict> Conflicts) : EnrollResult;
}

public sealed class EnrollmentApplication(IExecutionUnitRepository repository, IApplicationEventSink events, IEnrollmentGate gate)
{
    public Latent<EnrollResult> Enroll(EnrollCommand command) => Latent<EnrollResult>.DelayAsync(async cancellationToken =>
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var held = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var canonical = command.Capabilities.Select(Canonicalize).ToArray();
        var existingDefinitions = await repository.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var conflicts = canonical
            .Select(candidate => existingDefinitions.FirstOrDefault(existing => StringComparer.Ordinal.Equals(existing.Name, candidate.Name)) is { } existing &&
                !StringComparer.Ordinal.Equals(existing.ContractHash, candidate.ContractHash)
                    ? new CapabilityContractConflict(existing.Id, CapabilityContracts.GetDelta(existing, candidate))
                    : null)
            .Where(static conflict => conflict is not null)
            .Cast<CapabilityContractConflict>()
            .ToArray();
        if (conflicts.Length > 0)
        {
            return new EnrollResult.Conflict(conflicts);
        }

        var resolved = canonical.Select(candidate =>
        {
            var existing = existingDefinitions.FirstOrDefault(existing => StringComparer.Ordinal.Equals(existing.Name, candidate.Name));
            return existing is null ? candidate : candidate with { Id = existing.Id, ContractHash = existing.ContractHash };
        }).ToArray();
        var enrollment = new EnrollmentDefinition(command.Machine, resolved);
        var unit = await repository.GetAsync(command.ExecutionUnitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
        {
            unit = new ExecutionUnit(command.ExecutionUnitId, enrollment);
        }
        else
        {
            unit.ReplaceEnrollment(enrollment);
        }

        await repository.SaveAsync(unit, cancellationToken).ConfigureAwait(false);
        events.TriggerScheduler();
        return new EnrollResult.Enrolled(unit);
    });

    private static CapabilityDefinition Canonicalize(CapabilityDefinition definition)
    {
        definition.Validate();
        // Capability identifiers are server-owned.  A provider supplies a definition,
        // never an identifier that could collide with another provider's catalogue.
        return definition with
        {
            Id = CapabilityId.New(),
            ContractHash = CapabilityContracts.ComputeHash(definition.Inputs, definition.Output),
        };
    }
}
