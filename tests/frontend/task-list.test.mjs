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
  assert.equal(container.children[0].children[0].children[0].children[0].textContent, 'Styliser');
  assert.equal(container.children[0].children[0].children[1].textContent, 'Making progress');
  assert.equal(container.children[0].children[1].hidden, false);
  const reevaluate = container.children[0].children[2].children[0];
  assert.equal(reevaluate.textContent, 'Try matching again');
  await reevaluate.onclick();
  const result = container.children[1].children[2].children[0];
  assert.equal(result.textContent, 'Download result');
  let prevented = false;
  await result.onclick({ preventDefault: () => { prevented = true; } });

  assert.equal(prevented, true);
  assert.deepEqual(requests, [
    { url: '/api/tasks/running/reevaluate', init: { method: 'POST' } },
    { url: '/api/tasks/complete/result', init: undefined },
  ]);
  assert.deepEqual(opened, [['https://objects.example/result.zip', '_blank', 'noopener']]);
});

test('an empty poll result explains where newly queued work will appear', () => {
  const document = new Document();
  const container = new Element(document, 'div');

  renderTaskList(container, []);

  assert.equal(container.textContent, '');
  assert.equal(container.children.length, 1);
  assert.equal(container.children[0].children[0].textContent, 'Nothing on the reading list yet.');
  assert.equal(container.children[0].children[1].textContent, 'Create a task and its matching, progress, and result will appear here.');
});

test('failure details prefer the provider reason and translate legacy recovery codes', () => {
  const document = new Document();
  const container = new Element(document, 'div');

  renderTaskList(container, [
    { taskId: 'provider-failure', capabilityName: 'TripoSplat', status: 'Failed', attemptCount: 4, failureStep: 'triposplat', failureReason: 'The model manifest could not be downloaded.', canReevaluate: false, canRetrieveResult: false },
    { taskId: 'recovery-failure', capabilityName: 'TripoSplat', status: 'Failed', attemptCount: 4, failureStep: 'disconnect_recovery_expired', canReevaluate: false, canRetrieveResult: false },
  ]);

  assert.equal(container.children[0].children[1].children.at(-1).textContent, 'Needs attention: The model manifest could not be downloaded.');
  assert.equal(container.children[1].children[1].children.at(-1).textContent, 'Needs attention: The provider disconnected and did not reconnect before the recovery window expired.');
});
