import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const pagePath = new URL('../../src/MutualGPU.Api/wwwroot/index.html', import.meta.url);
const scriptPath = new URL('../../src/MutualGPU.Api/wwwroot/js/mutualgpu.js', import.meta.url);

test('capability catalogue refresh is explicit while task changes arrive through an event stream', async () => {
  const [page, script] = await Promise.all([
    readFile(pagePath, 'utf8'),
    readFile(scriptPath, 'utf8'),
  ]);

  assert.match(page, /id="refresh-capabilities"/);
  assert.match(script, /refreshCapabilitiesButton\.addEventListener\('click'/);
  assert.match(page, /mutualgpu\.js\?v=20260725-task-events1/);
  assert.match(script, /new EventSource\('\/api\/tasks\/events'\)/);
  assert.match(script, /events\.addEventListener\('tasks-changed'/);
  assert.doesNotMatch(script, /setInterval\(/);
});
