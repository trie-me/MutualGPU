import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const pagePath = new URL('../../src/MutualGPU.Api/wwwroot/index.html', import.meta.url);
const scriptPath = new URL('../../src/MutualGPU.Api/wwwroot/js/mutualgpu.js', import.meta.url);

test('capability catalogue refresh is explicit while task status keeps polling', async () => {
  const [page, script] = await Promise.all([
    readFile(pagePath, 'utf8'),
    readFile(scriptPath, 'utf8'),
  ]);

  assert.match(page, /id="refresh-capabilities"/);
  assert.match(script, /refreshCapabilitiesButton\.addEventListener\('click'/);
  assert.match(script, /setInterval\(\(\) => \{ void renderTasks\(\); \}, 2000\)/);
  assert.doesNotMatch(script, /setInterval\([^]*refreshCatalogue/);
});
