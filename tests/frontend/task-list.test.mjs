import assert from 'node:assert/strict';
import test from 'node:test';

import { renderTaskList } from '../../src/MutualGPU.Api/wwwroot/js/task-list.js';

class Element {
  constructor(ownerDocument, tagName) {
    this.ownerDocument = ownerDocument;
    this.tagName = tagName;
    this.children = [];
    this.textContent = '';
    this.href = '';
    this.attributes = new Map();
  }

  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; this.textContent = ''; }
  setAttribute(name, value) { this.attributes.set(name, value); }
}

class Document {
  createElement(tagName) { return new Element(this, tagName); }
  createTextNode(textContent) { return { textContent }; }
}

test('polled tasks render status, reevaluation, and result download behavior in one list', async () => {
  const document = new Document();
  const container = new Element(document, 'div');
  const requests = [];
  const opened = [];
  const fetchImpl = async (url, init) => {
    requests.push({ url, init });
    return { json: async () => ({ artifacts: [{ downloadUrl: 'https://objects.example/result.zip' }] }) };
  };
  renderTaskList(container, [
    { taskId: 'running', capabilityName: 'Styliser', status: 'Running', attemptCount: 1, canReevaluate: true, canRetrieveResult: false },
    { taskId: 'complete', capabilityName: 'Styliser', status: 'Completed', attemptCount: 2, canReevaluate: false, canRetrieveResult: true },
  ], { fetchImpl, openWindow: (...args) => opened.push(args) });

  assert.equal(container.children.length, 2);
  assert.equal(container.children[0].children[0].textContent, 'Styliser: Running');
  assert.equal(container.children[0].children[1].hidden, false);
  const reevaluate = container.children[0].children[2].children[0];
  assert.equal(reevaluate.textContent, 'Reevaluate');
  await reevaluate.onclick();
  const result = container.children[1].children[2].children[0];
  assert.equal(result.textContent, 'Result');
  let prevented = false;
  await result.onclick({ preventDefault: () => { prevented = true; } });

  assert.equal(prevented, true);
  assert.deepEqual(requests, [
    { url: '/api/tasks/running/reevaluate', init: { method: 'POST' } },
    { url: '/api/tasks/complete/result', init: undefined },
  ]);
  assert.deepEqual(opened, [['https://objects.example/result.zip', '_blank', 'noopener']]);
});

test('an empty poll result restores the no-tasks state', () => {
  const document = new Document();
  const container = new Element(document, 'div');

  renderTaskList(container, []);

  assert.equal(container.textContent, 'No tasks yet.');
  assert.deepEqual(container.children, []);
});
