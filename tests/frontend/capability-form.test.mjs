import assert from 'node:assert/strict';
import test from 'node:test';

import { createScalarPayload } from '../../src/MutualGPU.Api/wwwroot/js/capability-form.js';

test('submission scalars omit resource controls, files, and empty optional values', () => {
  const fakeFile = { name: 'source.png' };
  const scalars = createScalarPayload([
    ['style', 'Watercolour'],
    ['iterations', '12'],
    ['preserveColour', 'false'],
    ['preserveColour', 'true'],
    ['deadline', ''],
    ['compute', 'Large'],
    ['memory', 'Medium'],
    ['image', fakeFile],
  ]);

  assert.deepEqual(scalars, {
    style: 'Watercolour',
    iterations: '12',
    preserveColour: 'true',
  });
});
