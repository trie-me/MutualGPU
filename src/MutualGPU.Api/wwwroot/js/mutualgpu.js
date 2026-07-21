import { renderResourceGrid } from './resource-grid.js';
import { createFiberDiagnosticsOverlay } from './fiber-tree-overlay.js?v=20260721-forest16';
import { createScalarPayload } from './capability-form.js';
import { renderTaskList } from './task-list.js';

const select = document.querySelector('#capability-select');
const form = document.querySelector('#task-form');
const message = document.querySelector('#task-message');
const grid = document.querySelector('#resource-picker');
const taskList = document.querySelector('#task-list');
let catalogue = [];
let selectedResources = null;
let sort = { computeDescending: false, memoryDescending: true };

function control(input) {
  const label = document.createElement('label'); label.textContent = input.label;
  if (input.description) { const hint = document.createElement('small'); hint.textContent = input.description; label.append(hint); }
  let element;
  if (input.allowedValues?.length) { element = document.createElement('select'); input.allowedValues.forEach(value => element.add(new Option(value, value))); }
  else {
    element = document.createElement('input');
    element.type = input.type === 'Image' ? 'file' : input.type === 'Boolean' ? 'checkbox' : input.type === 'Integer' || input.type === 'Number' ? 'number' : input.type === 'Date' ? 'date' : input.type === 'DateTime' ? 'datetime-local' : 'text';
    if (input.type === 'Image') {
      element.accept = input.contentTypes?.join(',') || 'image/png,image/jpeg,image/webp';
      const preview = document.createElement('img'); preview.hidden = true; preview.alt = 'Selected image preview'; preview.className = 'input-image-preview';
      let previewUrl;
      element.addEventListener('change', async () => {
        element.setCustomValidity(''); preview.hidden = true;
        if (previewUrl) URL.revokeObjectURL(previewUrl);
        const file = element.files?.[0]; if (!file) return;
        if (input.contentTypes?.length && !input.contentTypes.includes(file.type)) { element.setCustomValidity('Choose a declared image type.'); return; }
        preview.onload = () => {
          if (preview.naturalWidth > 1024 || preview.naturalHeight > 1024) {
            element.setCustomValidity('Images must be at most 1024 by 1024 pixels.'); preview.hidden = true; return;
          }
          element.setCustomValidity(''); preview.hidden = false;
        };
        preview.onerror = () => { element.setCustomValidity('Choose a valid PNG, JPEG, or WebP image.'); preview.hidden = true; };
        previewUrl = URL.createObjectURL(file); preview.src = previewUrl;
      });
      label.append(preview);
    }
    if (input.type === 'DateTimeOffset') element.placeholder = '2026-07-21T12:00:00+01:00';
  }
  element.name = input.key; element.required = input.required && input.type !== 'Boolean'; if (input.default != null) element.value = input.default;
  if (input.type === 'Boolean') { const falseValue = document.createElement('input'); falseValue.type = 'hidden'; falseValue.name = input.key; falseValue.value = 'false'; element.value = 'true'; element.checked = String(input.default).toLowerCase() === 'true'; label.append(falseValue); }
  if (input.minimum != null) element.min = input.minimum; if (input.maximum != null) element.max = input.maximum;
  label.append(element); return label;
}

function selectedCapability() { return catalogue.find(item => item.capabilityId === select.value); }

function renderInputs() {
  const capability = selectedCapability();
  const inputs = document.querySelector('#dynamic-inputs');
  if (!capability) {
    inputs.replaceChildren();
    selectedResources = null;
    grid.replaceChildren();
    document.querySelector('#submit-task').disabled = true;
    return;
  }
  inputs.replaceChildren(...capability.inputs.map(control));
  selectedResources = null;
  renderResources();
}

function renderResources() {
  const capability = selectedCapability(); if (!capability) return;
  renderResourceGrid(grid, capability.machineAvailability, {
    selected: selectedResources,
    ...sort,
    onSelect(resources) { selectedResources = resources; renderResources(); document.querySelector('#submit-task').disabled = false; },
    onSort(nextSort) { sort = nextSort; renderResources(); },
  });
}

function validateStep(selector) {
  return [...document.querySelectorAll(`${selector} input, ${selector} select`)].every(element => element.reportValidity());
}

async function load() {
  const response = await fetch('/api/capabilities/'); catalogue = await response.json();
  const placeholder = new Option('Choose a task', '', true, true); placeholder.disabled = true;
  select.replaceChildren(placeholder, ...catalogue.map(capability => new Option(capability.name, capability.capabilityId)));
  renderInputs();
}
async function renderTasks() {
  try {
    const response = await fetch('/api/tasks/');
    if (!response.ok) { taskList.textContent = 'Unable to refresh your tasks.'; return; }
    renderTaskList(taskList, await response.json(), { onChanged: renderTasks });
  } catch { taskList.textContent = 'Unable to refresh your tasks.'; }
}

async function submissionError(response) {
  const problem = await response.json().catch(() => null);
  const fields = problem?.errors && Object.values(problem.errors).flat().filter(Boolean);
  if (fields?.length) return fields.join(' ');
  return problem?.detail || problem?.code || `Task could not be queued (HTTP ${response.status}).`;
}

select.addEventListener('change', () => { renderInputs(); message.textContent = ''; });
form.addEventListener('submit', async event => {
  event.preventDefault();
  if (!validateStep('[data-wizard-step="2"]')) return;
  if (!selectedResources) { message.textContent = 'Choose a resource tile before submitting.'; return; }
  const capability = selectedCapability(); const values = new FormData(form); const scalars = createScalarPayload(values.entries());
  const payload = new FormData(); payload.append('submission', JSON.stringify({ capabilityId: capability.capabilityId, contractHash: capability.contractHash, scalars, resources: selectedResources, idempotencyKey: crypto.randomUUID() }));
  [...values.entries()].filter(([, value]) => value instanceof File && value.size).forEach(([, value]) => payload.append('image', value));
  const response = await fetch('/api/tasks/', { method: 'POST', body: payload });
  message.textContent = response.ok ? 'Task queued.' : await submissionError(response);
  if (response.ok) {
    form.reset();
    select.value = '';
    renderInputs();
    await renderTasks();
    document.querySelector('#my-work').scrollIntoView({ behavior: 'smooth', block: 'start' });
  }
});
load().then(renderTasks).catch(() => { message.textContent = 'Capabilities are unavailable.'; });
setInterval(() => { void renderTasks(); }, 2000);
document.querySelector('#refresh-tasks').addEventListener('click', () => { void renderTasks(); });

createFiberDiagnosticsOverlay({ overlay: document.querySelector('#fiber-overlay'), tree: document.querySelector('#fiber-tree'), toggle: document.querySelector('#fiber-toggle'), close: document.querySelector('#fiber-close'), reset: document.querySelector('#fiber-reset'), fit: document.querySelector('#fiber-fit'), taskOnly: document.querySelector('#fiber-task-only'), pause: document.querySelector('#fiber-pause'), status: document.querySelector('#fiber-status'), summary: document.querySelector('#fiber-summary'), history: document.querySelector('#fiber-history'), simulations: document.querySelector('#fiber-simulations'), selection: document.querySelector('#fiber-selection'), treeDetail: document.querySelector('#fiber-tree-detail'), treeDetailBody: document.querySelector('#fiber-tree-detail-body'), treeDetailClose: document.querySelector('#fiber-tree-detail-close') });
