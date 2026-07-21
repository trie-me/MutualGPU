import assert from 'node:assert/strict';
import test from 'node:test';

import { createFiberDiagnosticsOverlay, renderFiberSnapshot } from '../../src/MutualGPU.Api/wwwroot/js/fiber-tree-overlay.js';

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
  createElement(tagName) { return new Element(this, tagName); }
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
    status: new Element(document, 'p'),
    summary: new Element(document, 'p'),
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

test('complete snapshots atomically render expandable scopes, fiber state, version, and truncation', () => {
  const { tree, summary } = fixture();

  renderFiberSnapshot(tree, summary, snapshot());

  assert.equal(tree.children.length, 2);
  const [root, warning] = tree.children;
  assert.equal(root.tagName, 'details');
  assert.equal(root.open, true);
  assert.equal(root.children[0].textContent, 'mutualgpu · Open');
  assert.equal(root.children[1].className, 'fiber-node fiber-node--faulted');
  assert.equal(root.children[1].children[0].textContent, 'provider-session');
  assert.match(root.children[1].children[1].textContent, /Faulted.*1\.5 s/);
  assert.equal(root.children[2].children[0].textContent, 'scheduler · Open');
  assert.equal(warning.className, 'fiber-tree__truncated');
  assert.match(summary.textContent, /^Snapshot 17 .* truncated$/);
});

test('opening, reconnecting, closing, and page shutdown own exactly one EventSource', () => {
  FakeEventSource.instances = [];
  const controls = fixture();
  const overlay = createFiberDiagnosticsOverlay({
    ...controls,
    pageLifecycle: controls.lifecycle,
    EventSourceType: FakeEventSource,
  });

  controls.toggle.dispatch('click');
  controls.toggle.dispatch('click');
  assert.equal(FakeEventSource.instances.length, 1);
  assert.equal(controls.overlay.hidden, false);
  assert.equal(controls.toggle.attributes.get('aria-expanded'), 'true');
  assert.equal(controls.status.textContent, 'Connecting…');

  const first = FakeEventSource.instances[0];
  first.dispatch('snapshot', { data: JSON.stringify(snapshot()) });
  assert.equal(controls.status.textContent, 'Connected');
  first.dispatch('error');
  assert.equal(controls.status.textContent, 'Reconnecting…');

  controls.close.dispatch('click');
  assert.equal(first.closeCount, 1);
  assert.equal(overlay.isStreaming(), false);
  assert.equal(controls.overlay.hidden, true);
  assert.equal(controls.status.textContent, 'Closed');

  overlay.open();
  assert.equal(FakeEventSource.instances.length, 2);
  controls.lifecycle.dispatch('pagehide');
  assert.equal(FakeEventSource.instances[1].closeCount, 1);
  assert.equal(overlay.isStreaming(), false);
});
