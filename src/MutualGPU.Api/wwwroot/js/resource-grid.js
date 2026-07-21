const macBookProfiles = Object.freeze([
  { tier: 'Small', label: 'M1-class', cpuCores: 8, gpuCores: 8, maximumMemoryGiB: 16 },
  { tier: 'Medium', label: 'M2 / M3-class', cpuCores: 8, gpuCores: 10, maximumMemoryGiB: 24 },
  { tier: 'Large', label: 'Pro-class', cpuCores: 12, gpuCores: 20, maximumMemoryGiB: 48 },
  { tier: 'ExtraLarge', label: 'Max-class', cpuCores: 16, gpuCores: 40, maximumMemoryGiB: 128 },
]);

// Actual WebGPU-capable Apple-silicon MacBook unified-memory configurations.
export const macBookMemoryGiB = Object.freeze([128, 96, 64, 48, 36, 32, 24, 18, 16, 8]);
export const resourceTiers = Object.freeze(macBookProfiles.map(profile => profile.tier));

const profileRank = tier => macBookProfiles.findIndex(profile => profile.tier === tier);
const profileFor = tier => macBookProfiles.find(profile => profile.tier === tier);
const profileText = profile => `${profile.cpuCores}-core CPU · ${profile.gpuCores}-core GPU`;

function availabilityFor(machines, profile, memoryGiB) {
  const matching = machines.filter(machine => profileRank(machine.computeTier) >= profileRank(profile.tier) && machine.memoryGiB >= memoryGiB);
  return { connectedCount: matching.reduce((total, machine) => total + machine.connectedCount, 0), idleCount: matching.reduce((total, machine) => total + machine.idleCount, 0) };
}

/** Renders a fixed MacBook WebGPU request grid; only live capacity changes. */
export function renderResourceGrid(container, machines, {
  selected = null,
  onSelect = () => {},
  computeDescending = false,
  memoryDescending = true,
  onSort = () => {},
} = {}) {
  container.replaceChildren();
  const profiles = [...macBookProfiles].sort((left, right) => computeDescending ? profileRank(right.tier) - profileRank(left.tier) : profileRank(left.tier) - profileRank(right.tier));
  const memory = [...macBookMemoryGiB].sort((left, right) => memoryDescending ? right - left : left - right);

  const controls = document.createElement('div'); controls.className = 'resource-grid__sort';
  const computeSort = document.createElement('button'); computeSort.type = 'button'; computeSort.className = 'resource-grid__sort-button'; computeSort.textContent = `CPU/GPU ${computeDescending ? 'high → low' : 'low → high'}`; computeSort.onclick = () => onSort({ computeDescending: !computeDescending, memoryDescending });
  const memorySort = document.createElement('button'); memorySort.type = 'button'; memorySort.className = 'resource-grid__sort-button'; memorySort.textContent = `Memory ${memoryDescending ? 'high → low' : 'low → high'}`; memorySort.onclick = () => onSort({ computeDescending, memoryDescending: !memoryDescending });
  controls.append(computeSort, memorySort);

  const grid = document.createElement('div'); grid.className = 'resource-grid'; grid.style.setProperty('--resource-columns', String(profiles.length)); grid.setAttribute('role', 'grid'); grid.setAttribute('aria-label', 'Choose CPU/GPU and memory resources');
  const corner = document.createElement('span'); corner.className = 'resource-grid__corner'; corner.textContent = 'Memory ↓ / CPU-GPU →'; grid.append(corner);
  profiles.forEach(profile => { const heading = document.createElement('span'); heading.className = 'resource-grid__heading'; heading.textContent = `${profile.label}\n${profileText(profile)}`; grid.append(heading); });

  memory.forEach(memoryGiB => {
    const label = document.createElement('span'); label.className = 'resource-grid__memory'; label.textContent = `${memoryGiB} GiB`; grid.append(label);
    profiles.forEach(profile => {
      if (memoryGiB > profile.maximumMemoryGiB) { const blank = document.createElement('span'); blank.className = 'resource-grid__empty'; blank.setAttribute('aria-hidden', 'true'); grid.append(blank); return; }
      const availability = availabilityFor(machines, profile, memoryGiB);
      const isSelected = selected?.computeTier === profile.tier && selected?.memoryGiB === memoryGiB;
      const tile = document.createElement('button'); tile.type = 'button';
      tile.className = `resource-tile${isSelected ? ' selected' : ''}${availability.idleCount === 0 ? ' resource-tile--waiting' : ''}`;
      tile.dataset.computeTier = profile.tier; tile.dataset.memoryGiB = String(memoryGiB); tile.setAttribute('role', 'gridcell'); tile.setAttribute('aria-pressed', String(isSelected)); tile.setAttribute('aria-label', `${profile.label}: ${profileText(profile)}, ${memoryGiB} GiB unified memory, ${availability.idleCount} ready of ${availability.connectedCount} connected`);
      const cpuGpu = document.createElement('strong'); cpuGpu.textContent = profileText(profile);
      const memoryText = document.createElement('span'); memoryText.textContent = `${memoryGiB} GiB unified memory`;
      const state = document.createElement('span'); state.textContent = availability.connectedCount === 0 ? 'No matching node · queued' : availability.idleCount === 0 ? `${availability.connectedCount} connected · wait` : `${availability.idleCount} ready · ${availability.connectedCount} connected`;
      tile.append(cpuGpu, memoryText, state); tile.onclick = () => onSelect({ computeTier: profile.tier, memoryGiB }); grid.append(tile);
    });
  });
  container.append(controls, grid);
}
