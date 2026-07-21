import assert from 'node:assert/strict';
import test from 'node:test';

class Element {
  constructor(tagName) {
    this.tagName = tagName;
    this.children = [];
    this.attributes = new Map();
    this.className = '';
    this.scope = '';
    this.textContent = '';
  }

  append(...children) {
    this.children.push(...children);
  }

  replaceChildren(...children) {
    this.children = children;
  }

  setAttribute(name, value) {
    this.attributes.set(name, value);
  }
}

globalThis.document = {
  createElement: (tagName) => new Element(tagName),
};

const { createResourceMatrix, memoryTiersForCompute, orderResourceTiers, renderResourceMatrix, resourceTiers } = await import('../../src/MutualGPU.Api/wwwroot/js/resource-grid.js');

test('resource controls only offer automatic or an available memory tier for the selected compute tier', () => {
  const resources = [
    { compute: 'Small', memory: 'Large' },
    { compute: 'Large', memory: 'Medium' },
    { compute: 'Large', memory: 'Large' },
  ];

  assert.deepEqual(resourceTiers, ['Automatic', 'Small', 'Medium', 'Large', 'ExtraLarge']);
  assert.deepEqual(orderResourceTiers(new Set(resources.map(item => item.compute))), ['Small', 'Large']);
  assert.deepEqual(memoryTiersForCompute(resources, 'Automatic'), ['Automatic']);
  assert.deepEqual(memoryTiersForCompute(resources, 'Large'), ['Medium', 'Large']);
});

test('named tiers retain memory x-axis ordering and descending compute y-axis ordering', () => {
  const matrix = createResourceMatrix([
    { compute: 'Small', memory: 'Large', connectedCount: 1, idleCount: 1 },
    { compute: 'ExtraLarge', memory: 'Small', connectedCount: 1, idleCount: 0 },
    { compute: 'Large', memory: 'Medium', connectedCount: 1, idleCount: 1 },
  ]);

  assert.deepEqual(matrix.memoryAxis, ['Small', 'Medium', 'Large']);
  assert.deepEqual(matrix.computeAxis.map(row => row.compute), ['ExtraLarge', 'Large', 'Small']);
});

test('capacity matrix renders memory across x and descending compute down y', () => {
  const container = new Element('div');
  renderResourceMatrix(container, {
    memoryAxis: ['Small', 'Large'],
    computeAxis: [
      { compute: 'Large', cells: [
        { memory: 'Small', connectedCount: 2, idleCount: 0, isSelectable: true },
        { memory: 'Large', connectedCount: 1, idleCount: 1, isSelectable: true },
      ] },
      { compute: 'Small', cells: [
        { memory: 'Small', connectedCount: 0, idleCount: 0, isSelectable: false },
        { memory: 'Large', connectedCount: 1, idleCount: 1, isSelectable: true },
      ] },
    ],
  });

  const [table] = container.children;
  const [head, body] = table.children;
  const [headerRow] = head.children;
  const [largeRow, smallRow] = body.children;

  assert.equal(table.attributes.get('aria-label'), 'Available CPU/GPU and memory capacity');
  assert.deepEqual(headerRow.children.map((cell) => cell.textContent), [
    'CPU/GPU tier ↓ · Memory tier →', 'Small', 'Large',
  ]);
  assert.equal(largeRow.children[0].textContent, 'Large');
  assert.equal(smallRow.children[0].textContent, 'Small');
  assert.equal(largeRow.children[1].textContent, '0 idle / 2 connected');
  assert.equal(largeRow.children[1].className, 'resource-capacity-matrix__waiting');
  assert.equal(smallRow.children[1].textContent, '—');
  assert.equal(smallRow.children[1].className, 'resource-capacity-matrix__unavailable');
});
