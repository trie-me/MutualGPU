const tierLabel = (tier) => String(tier).replace(/([a-z])([A-Z])/g, '$1 $2');
export const resourceTiers = Object.freeze(['Automatic', 'Small', 'Medium', 'Large', 'ExtraLarge']);
const tierRank = new Map([['Unspecified', 0], ...resourceTiers.map((tier, index) => [tier, index + 1])]);

export function orderResourceTiers(values, descending = false) {
  const order = (left, right) => (tierRank.get(left) ?? Number.MAX_SAFE_INTEGER) - (tierRank.get(right) ?? Number.MAX_SAFE_INTEGER);
  return [...values].sort(descending ? (left, right) => order(right, left) : order);
}

export function memoryTiersForCompute(resources, compute) {
  if (compute === 'Automatic') return ['Automatic'];
  return orderResourceTiers(new Set(resources.filter(item => item.compute === compute).map(item => item.memory)));
}

/** Builds the additional capacity view with named enum tiers from the public API. */
export function createResourceMatrix(resources) {
  const memoryAxis = orderResourceTiers(new Set(resources.map(item => item.memory)));
  const compute = orderResourceTiers(new Set(resources.map(item => item.compute)), true);
  return {
    memoryAxis,
    computeAxis: compute.map(computeTier => ({
      compute: computeTier,
      cells: memoryAxis.map(memory => {
        const value = resources.find(item => item.compute === computeTier && item.memory === memory);
        return { memory, connectedCount: value?.connectedCount ?? 0, idleCount: value?.idleCount ?? 0, isSelectable: !!value };
      }),
    })),
  };
}

/**
 * Renders the optional capacity view. It deliberately does not replace the task resource selector:
 * it lets requestors see real connected/idle supply by memory (x) and compute CPU/GPU tier (y).
 */
export function renderResourceMatrix(container, matrix) {
  container.replaceChildren();
  const table = document.createElement('table');
  table.className = 'resource-capacity-matrix';
  table.setAttribute('aria-label', 'Available CPU/GPU and memory capacity');

  const head = document.createElement('thead');
  const header = document.createElement('tr');
  const corner = document.createElement('th');
  corner.scope = 'col';
  corner.textContent = 'CPU/GPU tier ↓ · Memory tier →';
  header.append(corner);
  matrix.memoryAxis.forEach((tier) => {
    const cell = document.createElement('th');
    cell.scope = 'col';
    cell.textContent = tierLabel(tier);
    header.append(cell);
  });
  head.append(header);
  table.append(head);

  const body = document.createElement('tbody');
  matrix.computeAxis.forEach((row) => {
    const element = document.createElement('tr');
    const label = document.createElement('th');
    label.scope = 'row';
    label.textContent = tierLabel(row.compute);
    element.append(label);
    row.cells.forEach((entry) => {
      const cell = document.createElement('td');
      if (!entry.isSelectable) {
        cell.textContent = '—';
        cell.className = 'resource-capacity-matrix__unavailable';
      } else {
        cell.textContent = `${entry.idleCount} idle / ${entry.connectedCount} connected`;
        cell.className = entry.idleCount === 0 ? 'resource-capacity-matrix__waiting' : '';
      }
      element.append(cell);
    });
    body.append(element);
  });
  table.append(body);
  container.append(table);
}
