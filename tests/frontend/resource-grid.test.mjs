import assert from 'node:assert/strict';
import test from 'node:test';

class Element {
  constructor(tagName) {
    this.tagName = tagName;
    this.children = [];
    this.attributes = new Map();
    this.className = '';
    this.textContent = '';
    this.dataset = {};
    this.style = { setProperty() {} };
  }

  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; }
  setAttribute(name, value) { this.attributes.set(name, value); }
}

globalThis.document = { createElement: tagName => new Element(tagName) };

const { renderResourceGrid, resourceTiers } = await import('../../src/MutualGPU.Api/wwwroot/js/resource-grid.js');

test('resource picker uses the fixed MacBook CPU/GPU profiles and descending memory rows', () => {
  const container = new Element('div');
  renderResourceGrid(container, [
    { computeTier: 'Small', memoryGiB: 64, connectedCount: 1, idleCount: 1 },
    { computeTier: 'Large', memoryGiB: 8, connectedCount: 1, idleCount: 0 },
    { computeTier: 'Large', memoryGiB: 64, connectedCount: 1, idleCount: 1 },
  ]);

  assert.deepEqual(resourceTiers, ['Small', 'Medium', 'Large', 'ExtraLarge']);
  const [, grid] = container.children;
  assert.equal(grid.attributes.get('aria-label'), 'Choose CPU/GPU and memory resources');
  assert.equal(grid.children[0].textContent, 'Memory ↓ / CPU-GPU →');
  assert.match(grid.children[1].textContent, /M1-class/);
  assert.match(grid.children[4].textContent, /Max-class/);
  assert.equal(grid.children[5].textContent, '128 GiB');
  assert.equal(grid.children.find(cell => cell.textContent === '8 GiB').textContent, '8 GiB');
});

test('resource tiles select the exact advertised profile and surface queueing state', () => {
  const container = new Element('div');
  let selected;
  renderResourceGrid(container, [
    { computeTier: 'Large', memoryGiB: 32, connectedCount: 2, idleCount: 1 },
  ], { onSelect: profile => { selected = profile; } });

  const [, grid] = container.children;
  const tile = grid.children.find(child => child.dataset.computeTier === 'Large' && child.dataset.memoryGiB === '32');
  assert.equal(tile.className, 'resource-tile');
  assert.equal(tile.children[0].textContent, '12-core CPU · 20-core GPU');
  assert.equal(tile.children[1].textContent, '32 GiB unified memory');
  assert.equal(tile.children[2].textContent, '1 ready · 2 connected');
  tile.onclick();
  assert.deepEqual(selected, { computeTier: 'Large', memoryGiB: 32 });
});

test('axis sort buttons provide a reversible resource ordering', () => {
  const container = new Element('div');
  let nextSort;
  renderResourceGrid(container, [
    { computeTier: 'Small', memoryGiB: 8, connectedCount: 1, idleCount: 1 },
  ], { onSort: value => { nextSort = value; } });

  const [controls] = container.children;
  assert.equal(controls.children[0].textContent, 'CPU/GPU low → high');
  assert.equal(controls.children[1].textContent, 'Memory high → low');
  controls.children[0].onclick();
  assert.deepEqual(nextSort, { computeDescending: true, memoryDescending: true });
});
