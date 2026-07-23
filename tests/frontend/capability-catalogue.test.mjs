import assert from 'node:assert/strict';
import test from 'node:test';

import { reconcileCatalogueSelection } from '../../src/MutualGPU.Api/wwwroot/js/capability-catalogue.js';

const resources = { computeTier: 'High', memoryGiB: 16 };

test('keeps a selected capability and resource while a matching provider remains connected', () => {
  const catalogue = [{
    capabilityId: 'gaussian-splat',
    machineAvailability: [{ ...resources, connectedCount: 1, idleCount: 0 }],
  }];

  const result = reconcileCatalogueSelection(catalogue, 'gaussian-splat', resources);

  assert.equal(result.selectedCapability, catalogue[0]);
  assert.deepEqual(result.selectedResources, resources);
  assert.equal(result.selectionRemoved, false);
});

test('clears a resource choice when its provider-backed resource class disappears', () => {
  const catalogue = [{
    capabilityId: 'gaussian-splat',
    machineAvailability: [{ computeTier: 'Standard', memoryGiB: 8, connectedCount: 1, idleCount: 1 }],
  }];

  const result = reconcileCatalogueSelection(catalogue, 'gaussian-splat', resources);

  assert.equal(result.selectedCapability, catalogue[0]);
  assert.equal(result.selectedResources, null);
  assert.equal(result.selectionRemoved, false);
});

test('removes the selection when the capability has no connected providers', () => {
  const result = reconcileCatalogueSelection([], 'gaussian-splat', resources);

  assert.equal(result.selectedCapability, null);
  assert.equal(result.selectedResources, null);
  assert.equal(result.selectionRemoved, true);
});
