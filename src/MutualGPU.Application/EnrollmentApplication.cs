using MutualGPU.Domain;
using NetCats.Core;

namespace MutualGPU.Application;

public sealed record EnrollCommand(ExecutionUnitId ExecutionUnitId, MachineProfile Machine, IReadOnlyList<CapabilityDefinition> Capabilities);

public abstract record EnrollResult
{
    public sealed record Enrolled(ExecutionUnit Unit) : EnrollResult;

    public sealed record Conflict(IReadOnlyList<CapabilityContractConflict> Conflicts) : EnrollResult;
}

public sealed class EnrollmentApplication(
    IExecutionUnitRepository repository,
    IApplicationEventSink events,
    IEnrollmentGate? gate = null,
    IOperationUnitOfWork? operations = null)
{
    public Latent<EnrollResult> Enroll(EnrollCommand command) => Latent<EnrollResult>.DelayAsync(async cancellationToken =>
    {
        ArgumentNullException.ThrowIfNull(command);
        if (operations is not null)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return await operations.ExecuteAsync<EnrollResult>(
                        async (context, token) =>
                        {
                            var canonical = command.Capabilities.Select(Canonicalize).ToArray();
                            var existingDefinitions = await context.Capabilities.GetAllAsync(token).ConfigureAwait(false);
                            var resolved = canonical.Select(candidate =>
                            {
                                var existing = existingDefinitions.FirstOrDefault(existing =>
                                    StringComparer.Ordinal.Equals(existing.Name, candidate.Name));
                                if (existing is null) context.Capabilities.Add(candidate);
                                return existing ?? candidate;
                            }).ToArray();
                            var enrollment = new EnrollmentDefinition(command.Machine, resolved);
                            var unit = await context.ExecutionUnits
                                .GetAsync(command.ExecutionUnitId, token)
                                .ConfigureAwait(false);
                            if (unit is null)
                            {
                                unit = new ExecutionUnit(command.ExecutionUnitId, enrollment);
                                context.ExecutionUnits.Add(unit);
                            }
                            else
                            {
                                unit.ReplaceEnrollment(enrollment);
                                context.ExecutionUnits.Update(unit);
                            }

                            context.Publish(OperationEvent.Scheduler(DateTimeOffset.UtcNow));
                            return new EnrollResult.Enrolled(unit);
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (CapabilityNameConflictException) when (attempt == 0)
                {
                }
                catch (OptimisticConcurrencyException) when (attempt == 0)
                {
                }
            }

            throw new InvalidOperationException("Enrollment could not be committed after reloading canonical PostgreSQL state.");
        }

        if (gate is null)
        {
            throw new InvalidOperationException("A legacy enrollment gate is required when PostgreSQL operations are not configured.");
        }
        await using var held = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var canonical = command.Capabilities.Select(Canonicalize).ToArray();
        var existingDefinitions = await repository.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var resolved = canonical.Select(candidate =>
        {
            var existing = existingDefinitions.FirstOrDefault(existing => StringComparer.Ordinal.Equals(existing.Name, candidate.Name));
            // Capability names form a shared public catalogue. Until capability
            // versioning is introduced, the first enrolled contract owns that
            // name; every later provider is enrolled against it verbatim.
            return existing ?? candidate;
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
        // Capability identifiers are server-owned.  A provider supplies a definition,
        // never an identifier that could collide with another provider's catalogue.
        // Compute the contract digest before validation: SDK callers deliberately omit
        // this server-owned continuity field from their enrollment definition.
        var canonical = definition with
        {
            Id = CapabilityId.New(),
            ContractHash = CapabilityContracts.ComputeHash(definition.Inputs, definition.Output),
        };
        canonical.Validate();
        return canonical;
    }
}
