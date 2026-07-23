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
  insertBefore(child, reference) {
    const index = this.children.indexOf(reference);
    this.children.splice(index < 0 ? this.children.length : index, 0, child);
  }
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
    return { json: async () => ({ artifacts: [{ name: 'result', downloadUrl: 'https://objects.example/result.zip' }, { name: 'preview', downloadUrl: 'https://objects.example/preview.png' }] }) };
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
  await new Promise(resolve => setImmediate(resolve));
  const preview = container.children[1].children[2];
  assert.equal(preview.tagName, 'img');
  assert.equal(preview.src, 'https://objects.example/preview.png');
  assert.equal(preview.alt, 'Result preview for Styliser');
  preview.onerror();
  assert.equal(preview.src, '/images/no-result-preview.png');
  assert.equal(preview.alt, 'No result preview is available for Styliser');
  const result = container.children[1].children[3].children[0];
  assert.equal(result.textContent, 'Download result');
  let prevented = false;
  await result.onclick({ preventDefault: () => { prevented = true; } });

  assert.equal(prevented, true);
  assert.deepEqual(requests, [
    { url: '/api/tasks/complete/result', init: undefined },
    { url: '/api/tasks/running/reevaluate', init: { method: 'POST' } },
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

test('a completed result without a preview artifact renders the no-preview image', async () => {
  const document = new Document();
  const container = new Element(document, 'div');
  renderTaskList(container, [
    { taskId: 'zip-only', capabilityName: 'Point cloud', status: 'Completed', attemptCount: 1, canReevaluate: false, canRetrieveResult: true },
  ], { fetchImpl: async () => ({ json: async () => ({ artifacts: [{ name: 'result', downloadUrl: 'https://objects.example/result.zip' }] }) }) });

  await new Promise(resolve => setImmediate(resolve));
  const preview = container.children[0].children[2];
  assert.equal(preview.src, '/images/no-result-preview.png');
  assert.equal(preview.alt, 'No result preview is available for Point cloud');
});

test('failure details prefer the provider reason and translate legacy recovery codes', () => {
  const document = new Document();
  const container = new Element(document, 'div');

  renderTaskList(container, [
    { taskId: 'provider-failure', capabilityName: 'TripoSplat', status: 'Failed', attemptCount: 4, failureStep: 'triposplat', failureReason: 'The model manifest could not be downloaded.', canReevaluate: false, canRetrieveResult: false },
    { taskId: 'recovery-failure', capabilityName: 'TripoSplat', status: 'Failed', attemptCount: 4, failureStep: 'disconnect_recovery_expired', canReevaluate: false, canRetrieveResult: false },
    { taskId: 'safety-failure', capabilityName: 'FLUX.2', status: 'Failed', attemptCount: 1, failureStep: 'content_safety', failureReason: 'The generated image was blocked as inappropriate content.', canReevaluate: false, canRetrieveResult: false },
  ]);

  assert.equal(container.children[0].children[1].children.at(-1).textContent, 'Needs attention: The model manifest could not be downloaded.');
  assert.equal(container.children[1].children[1].children.at(-1).textContent, 'Needs attention: The provider disconnected and did not reconnect before the recovery window expired.');
  assert.equal(container.children[2].children[0].children[1].textContent, 'Inappropriate content');
  assert.equal(container.children[2].children[1].children.at(-1).textContent, 'Needs attention: The generated image was blocked as inappropriate content.');
});
