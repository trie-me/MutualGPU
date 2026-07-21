import { createResourceMatrix, memoryTiersForCompute, orderResourceTiers, renderResourceMatrix } from './resource-grid.js';
import { createFiberDiagnosticsOverlay } from './fiber-tree-overlay.js';
import { createScalarPayload } from './capability-form.js';
import { renderTaskList } from './task-list.js';

const select = document.querySelector('#capability-select');
const form = document.querySelector('#task-form');
const message = document.querySelector('#task-message');
const matrix = document.querySelector('#resource-capacity-matrix');
const taskList = document.querySelector('#task-list');
let catalogue = [];

function control(input) {
  const label = document.createElement('label'); label.textContent = input.label;
  let element;
  if (input.allowedValues?.length) { element = document.createElement('select'); input.allowedValues.forEach(value => { const option = new Option(value, value); element.add(option); }); }
  else {
    element = document.createElement('input');
    element.type = input.type === 'Image' ? 'file' : input.type === 'Boolean' ? 'checkbox' : input.type === 'Integer' || input.type === 'Number' ? 'number' : input.type === 'Date' ? 'date' : input.type === 'DateTime' ? 'datetime-local' : 'text';
    if (input.type === 'Image') {
      element.accept = input.contentTypes?.join(',') || 'image/png,image/jpeg,image/webp';
      const preview = document.createElement('img'); preview.hidden = true; preview.alt = 'Selected image preview'; preview.className = 'input-image-preview';
      element.addEventListener('change', async () => {
        element.setCustomValidity(''); preview.hidden = true;
        const file = element.files?.[0]; if (!file) return;
        if (input.contentTypes?.length && !input.contentTypes.includes(file.type)) { element.setCustomValidity('Choose a declared image type.'); return; }
        if (typeof createImageBitmap === 'function') {
          try {
            const bitmap = await createImageBitmap(file);
            const valid = bitmap.width <= 1024 && bitmap.height <= 1024; bitmap.close();
            if (!valid) { element.setCustomValidity('Images must be at most 1024 by 1024 pixels.'); return; }
          } catch { element.setCustomValidity('Choose a valid PNG, JPEG, or WebP image.'); return; }
        }
        preview.src = URL.createObjectURL(file); preview.hidden = false;
      });
      label.append(preview);
    }
    if (input.type === 'DateTimeOffset') element.placeholder = '2026-07-21T12:00:00+01:00';
  }
  element.name = input.key; element.required = input.required && input.type !== 'Boolean'; if (input.default != null) element.value = input.default;
  if (input.type === 'Boolean') {
    const falseValue = document.createElement('input'); falseValue.type = 'hidden'; falseValue.name = input.key; falseValue.value = 'false';
    element.value = 'true'; element.checked = String(input.default).toLowerCase() === 'true'; label.append(falseValue);
  }
  if (input.minimum != null) element.min = input.minimum; if (input.maximum != null) element.max = input.maximum;
  label.append(element); return label;
}

function showCapability() {
  const capability = catalogue.find(item => item.capabilityId === select.value); if (!capability) return;
  form.replaceChildren(); form.enctype = 'multipart/form-data'; capability.inputs.forEach(input => form.append(control(input)));
  const computeValues = orderResourceTiers(new Set(capability.resourceAvailability.map(item => item.compute)));
  const compute = document.createElement('select'); compute.name = 'compute'; ['Automatic', ...computeValues].forEach(tier => compute.add(new Option(tier, tier)));
  const memory = document.createElement('select'); memory.name = 'memory';
  const refreshMemory = () => {
    const allowed = memoryTiersForCompute(capability.resourceAvailability, compute.value);
    memory.replaceChildren(...allowed.map(tier => new Option(tier, tier)));
  };
  compute.addEventListener('change', refreshMemory); refreshMemory();
  form.append('Compute ', compute, ' Memory ', memory);
  const submit = document.createElement('button'); submit.type = 'submit'; submit.textContent = 'Queue task'; form.append(submit);
  renderResourceMatrix(matrix, createResourceMatrix(capability.resourceAvailability));
}

async function load() {
  const response = await fetch('/api/capabilities/'); catalogue = await response.json();
  select.replaceChildren(...catalogue.map(capability => new Option(capability.name, capability.capabilityId)));
  showCapability();
}
async function renderTasks() {
  const response = await fetch('/api/tasks/'); if (!response.ok) return;
  renderTaskList(taskList, await response.json());
}
select.addEventListener('change', showCapability);
form.addEventListener('submit', async event => { event.preventDefault(); const capability = catalogue.find(item => item.capabilityId === select.value); const values = new FormData(form); const scalars = createScalarPayload(values.entries());
  const payload = new FormData(); payload.append('submission', JSON.stringify({ capabilityId: capability.capabilityId, contractHash: capability.contractHash, scalars, compute: values.get('compute'), memory: values.get('memory'), idempotencyKey: crypto.randomUUID() })); [...values.entries()].filter(([, value]) => value instanceof File && value.size).forEach(([, value]) => payload.append('image', value));
  const response = await fetch('/api/tasks/', { method: 'POST', body: payload });
  message.textContent = response.ok ? 'Task queued.' : 'Task could not be queued.';
});
load().then(renderTasks).catch(() => { message.textContent = 'Capabilities are unavailable.'; });
setInterval(() => { void renderTasks(); }, 2000);

createFiberDiagnosticsOverlay({
  overlay: document.querySelector('#fiber-overlay'),
  tree: document.querySelector('#fiber-tree'),
  toggle: document.querySelector('#fiber-toggle'),
  close: document.querySelector('#fiber-close'),
  status: document.querySelector('#fiber-status'),
  summary: document.querySelector('#fiber-summary'),
});
