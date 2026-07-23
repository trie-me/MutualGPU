export function reconcileCatalogueSelection(catalogue, capabilityId, selectedResources) {
  const selectedCapability = catalogue.find(item => item.capabilityId === capabilityId) ?? null;
  const resourceStillAvailable = selectedCapability && selectedResources
    ? selectedCapability.machineAvailability.some(machine =>
        machine.computeTier === selectedResources.computeTier && machine.memoryGiB === selectedResources.memoryGiB)
    : false;

  return {
    selectedCapability,
    selectedResources: resourceStillAvailable ? selectedResources : null,
    selectionRemoved: Boolean(capabilityId && !selectedCapability),
  };
}
