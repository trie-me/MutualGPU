import assert from 'node:assert/strict';
import test from 'node:test';

import { buildForestNodes, createFiberDiagnosticsOverlay } from '../../src/MutualGPU.Api/wwwroot/js/fiber-tree-overlay.js';

class EventTarget {
  listeners = new Map();

  addEventListener(name, listener) {
    const listeners = this.listeners.get(name) || [];
    listeners.push(listener);
    this.listeners.set(name, listeners);
  }

  removeEventListener(name, listener) {
    this.listeners.set(name, (this.listeners.get(name) || []).filter(candidate => candidate !== listener));
  }

  dispatch(name, event = {}) {
    for (const listener of this.listeners.get(name) || []) listener(event);
  }
}

class Element extends EventTarget {
  constructor(ownerDocument, tagName) {
    super();
    this.ownerDocument = ownerDocument;
    this.tagName = tagName;
    this.children = [];
    this.attributes = new Map();
    this.className = '';
    this.dataset = {};
    this.hidden = false;
    this.open = false;
    this.textContent = '';
  }

  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; }
  setAttribute(name, value) { this.attributes.set(name, value); }
}

class Document {
  constructor() {
    this.body = { classList: new SetClassList() };
  }

  createElement(tagName) { return new Element(this, tagName); }
}

class SetClassList {
  values = new Set();

  add(value) { this.values.add(value); }
  remove(value) { this.values.delete(value); }
  contains(value) { return this.values.has(value); }
}

class FakeEventSource extends EventTarget {
  static CLOSED = 2;
  static instances = [];

  constructor(url) {
    super();
    this.url = url;
    this.readyState = 0;
    this.closeCount = 0;
    FakeEventSource.instances.push(this);
  }

  close() {
    this.readyState = FakeEventSource.CLOSED;
    this.closeCount += 1;
  }
}

function fixture() {
  const document = new Document();
  return {
    overlay: new Element(document, 'aside'),
    tree: new Element(document, 'div'),
    toggle: new Element(document, 'button'),
    close: new Element(document, 'button'),
    reset: new Element(document, 'button'),
    fit: new Element(document, 'button'),
    taskOnly: new Element(document, 'button'),
    pause: new Element(document, 'button'),
    status: new Element(document, 'p'),
    summary: new Element(document, 'p'),
    history: new Element(document, 'div'),
    selection: new Element(document, 'p'),
    treeDetail: new Element(document, 'section'),
    treeDetailBody: new Element(document, 'div'),
    treeDetailClose: new Element(document, 'button'),
    lifecycle: new EventTarget(),
  };
}

function snapshot() {
  return {
    version: 17,
    observedAt: '2026-07-21T12:00:03.000Z',
    isTruncated: true,
    roots: [{
      name: 'mutualgpu',
      state: 'Open',
      scopes: [{ name: 'scheduler', state: 'Open', scopes: [], fibers: [] }],
      fibers: [{
        name: 'provider-session',
        state: 'Terminated',
        outcome: 'Faulted',
        errorCategory: 'IOException',
        startedAt: '2026-07-21T12:00:01.000Z',
        completedAt: '2026-07-21T12:00:02.500Z',
      }],
    }],
  };
}

test('snapshots become stable scope and fiber nodes for the zoomable forest', () => {
  const nodes = buildForestNodes(snapshot());
  const root = nodes.find(node => node.id === 'scope:mutualgpu:0');
  const scheduler = nodes.find(node => node.name === 'scheduler');
  const provider = nodes.find(node => node.name === 'provider-session');

  assert.equal(root.kind, 'scope');
  assert.equal(scheduler.parentId, root.id);
  assert.equal(provider.kind, 'fiber');
  assert.equal(provider.state, 'faulted');
  assert.equal(provider.error, 'IOException');
  assert.notEqual(provider.x, root.x);
  assert.ok(provider.y > root.y);
});

test('forest lays sibling activity across the canvas rather than as a narrow outline', () => {
  const nodes = buildForestNodes({
    roots: [{ name: 'mutualgpu', state: 'Open', fibers: [], scopes: [{
      name: 'scheduler', state: 'Open', fibers: [], scopes: [
        { name: 'scheduler-evaluation', state: 'Closed', fibers: [], scopes: [] },
        { name: 'scheduler-evaluation', state: 'Closed', fibers: [], scopes: [] },
      ],
    }], }],
  });
  const evaluations = nodes.filter(node => node.name === 'scheduler-evaluation');
  assert.equal(evaluations.length, 2);
  assert.notEqual(evaluations[0].x, evaluations[1].x);
  assert.equal(evaluations[0].y, evaluations[1].y);
});

test('task-only forest retains task activity and its ownership path', () => {
  const nodes = buildForestNodes({
    roots: [{
      name: 'mutualgpu', state: 'Open', fibers: [{ name: 'unrelated-host-fiber' }], scopes: [{
        name: 'provider-sessions', state: 'Open', fibers: [], scopes: [{
          name: 'provider-session', state: 'Open', fibers: [{ name: 'provider-session' }], scopes: [{
            name: 'task-attempt', state: 'Open', fibers: [{ name: 'task-abc12345', state: 'Running' }], scopes: [{
              name: 'task-result-upload', state: 'Closed', fibers: [{ name: 'authorize result upload', state: 'Terminated' }], scopes: [],
            }],
          }],
        }],
      }],
    }],
  }, { taskOnly: true });

  assert.deepEqual(nodes.map(node => node.name), ['mutualgpu', 'provider-sessions', 'provider-session', 'task-attempt', 'task-abc12345', 'task-result-upload', 'authorize result upload']);
});

test('opening, reconnecting, closing, and page shutdown own exactly one EventSource', () => {
  FakeEventSource.instances = [];
  const controls = fixture();
  const overlay = createFiberDiagnosticsOverlay({
    ...controls,
    pageLifecycle: controls.lifecycle,
    EventSourceType: FakeEventSource,
    storage: new MemoryStorage(),
  });

  controls.toggle.dispatch('click');
  controls.toggle.dispatch('click');
  assert.equal(FakeEventSource.instances.length, 1);
  assert.equal(controls.overlay.hidden, false);
  assert.equal(controls.overlay.ownerDocument.body.classList.contains('fiber-forest-open'), true);
  assert.equal(controls.toggle.attributes.get('aria-expanded'), 'true');
  assert.equal(controls.status.textContent, 'Connecting…');
  controls.taskOnly.dispatch('click');
  assert.equal(controls.taskOnly.attributes.get('aria-pressed'), 'true');

  const first = FakeEventSource.instances[0];
  first.dispatch('snapshot', { data: JSON.stringify(snapshot()) });
  assert.equal(controls.status.textContent, 'Connected');
  controls.pause.dispatch('click');
  assert.equal(controls.pause.attributes.get('aria-pressed'), 'true');
  const newer = { ...snapshot(), version: 18, observedAt: '2026-07-21T12:00:05.000Z' };
  first.dispatch('snapshot', { data: JSON.stringify(newer) });
  assert.equal(controls.history.children.length, 2);
  assert.match(controls.summary.textContent, /^Paused snapshot 17/);
  controls.pause.dispatch('click');
  assert.match(controls.summary.textContent, /^Live snapshot 18/);
  assert.equal(controls.history.children.length, 3);
  controls.history.children[1].dispatch('click');
  assert.match(controls.summary.textContent, /^Historical snapshot 17/);
  controls.history.children[0].dispatch('click');
  assert.match(controls.summary.textContent, /^Live snapshot 18/);
  first.dispatch('error');
  assert.equal(controls.status.textContent, 'Reconnecting…');

  controls.close.dispatch('click');
  assert.equal(first.closeCount, 1);
  assert.equal(overlay.isStreaming(), false);
  assert.equal(controls.overlay.hidden, true);
  assert.equal(controls.overlay.ownerDocument.body.classList.contains('fiber-forest-open'), false);
  assert.equal(controls.status.textContent, 'Closed');

  overlay.open();
  assert.equal(FakeEventSource.instances.length, 2);
  controls.lifecycle.dispatch('pagehide');
  assert.equal(FakeEventSource.instances[1].closeCount, 1);
  assert.equal(overlay.isStreaming(), false);
});

class MemoryStorage {
  values = new Map();

  getItem(key) { return this.values.get(key) || null; }
  setItem(key, value) { this.values.set(key, value); }
}
