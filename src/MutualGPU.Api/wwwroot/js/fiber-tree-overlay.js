const COLORS = Object.freeze({ scope: '#77b8ff', running: '#68e39b', terminal: '#b785ff', faulted: '#ff9bee', cancelled: '#ffce6b', edge: '#4d8fca', text: '#d9efff', muted: '#8fa9cd', selected: '#e0c5ff' });
const TASK_SCOPE_NAMES = new Set(['scheduler-evaluation', 'task-attempt', 'task-acceptance', 'task-phase', 'task-input-download', 'task-result-upload', 'task-completion', 'task-failure', 'simulated-task', 'simulated-input', 'simulated-inference', 'simulated-packaging', 'simulated-upload']);
const HISTORY_KEY = 'mutualgpu.fiber-history.v1';
const HISTORY_LIMIT = 24;

function fiberState(fiber) { return String(fiber.outcome || fiber.state || 'Unknown').toLowerCase(); }
function fiberColor(fiber) {
  const state = fiberState(fiber);
  if (state.includes('fault')) return COLORS.faulted;
  if (state.includes('cancel')) return COLORS.cancelled;
  return state.includes('succeed') || state.includes('terminat') ? COLORS.terminal : COLORS.running;
}

function renderFiber(document, fiber, observedAt) {
  const element = document.createElement('div'); element.className = `fiber-node fiber-node--${fiberState(fiber)}`;
  const name = document.createElement('strong'); name.textContent = fiber.name;
  const detail = document.createElement('span'); detail.textContent = ` · ${fiber.outcome || fiber.state || 'Unknown'} · ${new Date(fiber.startedAt).toLocaleTimeString()}`;
  element.append(name, detail);
  if (fiber.errorCategory) { const error = document.createElement('span'); error.className = 'fiber-node__error'; error.textContent = ` · ${fiber.errorCategory}`; element.append(error); }
  return element;
}

function renderScope(document, scope, observedAt) {
  const element = document.createElement('details'); element.className = `fiber-scope fiber-scope--${String(scope.state).toLowerCase()}`; element.open = true;
  const heading = document.createElement('summary'); heading.textContent = `${scope.name} · ${scope.state}`; element.append(heading);
  (scope.fibers || []).forEach(fiber => element.append(renderFiber(document, fiber, observedAt)));
  (scope.scopes || []).forEach(child => element.append(renderScope(document, child, observedAt)));
  return element;
}

export function renderLogicalRuntimeTree(container, snapshot) {
  const document = container.ownerDocument;
  const nodes = (snapshot.roots || []).map(scope => renderScope(document, scope, snapshot.observedAt));
  if (!nodes.length) { const empty = document.createElement('p'); empty.textContent = 'No logical scopes are currently visible.'; nodes.push(empty); }
  container.replaceChildren(...nodes);
}

/** Converts the diagnostics snapshot into stable, positioned forest nodes. */
function taskOnlySnapshot(snapshot) {
  const keepScope = scope => {
    const childScopes = (scope.scopes || []).map(keepScope).filter(Boolean);
    if (TASK_SCOPE_NAMES.has(scope.name)) return { ...scope, scopes: childScopes };
    if (!childScopes.length) return null;
    return { ...scope, scopes: childScopes, fibers: [] };
  };
  return { ...snapshot, roots: (snapshot.roots || []).map(keepScope).filter(Boolean) };
}

export function buildForestNodes(snapshot, { taskOnly = false } = {}) {
  const nodes = [];
  let leaf = 0;
  const leafGap = 190;
  const visitScope = (scope, parentId, depth, path) => {
    const id = `scope:${path}`;
    const node = { id, parentId, kind: 'scope', name: scope.name, state: scope.state, depth, color: COLORS.scope };
    nodes.push(node);
    const children = [];
    (scope.fibers || []).forEach((fiber, index) => {
      const child = { id: `fiber:${path}:${fiber.name}:${index}`, parentId: id, kind: 'fiber', name: fiber.name, state: fiberState(fiber), depth: depth + 1, color: fiberColor(fiber), error: fiber.errorCategory, startedAt: fiber.startedAt, completedAt: fiber.completedAt, x: 72 + leaf++ * leafGap, y: 68 + (depth + 1) * 94 };
      nodes.push(child); children.push(child);
    });
    (scope.scopes || []).forEach((child, index) => children.push(visitScope(child, id, depth + 1, `${path}/${child.name}:${index}`)));
    node.x = children.length ? children.reduce((total, child) => total + child.x, 0) / children.length : 72 + leaf++ * leafGap;
    node.y = 68 + depth * 94;
    return node;
  };
  const forestSnapshot = taskOnly ? taskOnlySnapshot(snapshot) : snapshot;
  (forestSnapshot.roots || []).forEach((root, index) => visitScope(root, null, 0, `${root.name}:${index}`));
  return nodes;
}

function createForestRenderer(canvas, onNodeClick) {
  const context = canvas.getContext?.('2d');
  if (!context) return { update() {}, resetView() {}, fitView() {}, setTaskOnly() {}, destroy() {} };
  const nodes = new Map();
  let view = { x: 0, y: 0, zoom: 1 };
  let dragging;
  let frame;
  let selected;
  let hasFitted = false;
  let emptyMessage = 'Waiting for the first runtime snapshot…';
  let latestSnapshot;
  let taskOnly = false;
  const requestFrame = globalThis.requestAnimationFrame?.bind(globalThis) || (callback => setTimeout(callback, 16));
  const cancelFrame = globalThis.cancelAnimationFrame?.bind(globalThis) || clearTimeout;

  const resize = () => {
    const bounds = canvas.getBoundingClientRect();
    const ratio = globalThis.devicePixelRatio || 1;
    canvas.width = Math.max(1, Math.floor(bounds.width * ratio));
    canvas.height = Math.max(1, Math.floor(bounds.height * ratio));
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    draw();
  };
  const canvasPoint = event => {
    const bounds = canvas.getBoundingClientRect();
    return { x: (event.clientX - bounds.left - view.x) / view.zoom, y: (event.clientY - bounds.top - view.y) / view.zoom };
  };
  const draw = () => {
    frame = undefined;
    const bounds = canvas.getBoundingClientRect();
    context.clearRect(0, 0, bounds.width, bounds.height);
    context.save(); context.translate(view.x, view.y); context.scale(view.zoom, view.zoom);
    const visible = [...nodes.values()].filter(node => node.opacity > .01);
    if (!visible.length) {
      context.fillStyle = COLORS.muted;
      context.font = '600 14px ui-sans-serif, system-ui';
      context.textAlign = 'center';
      context.fillText(emptyMessage, bounds.width / 2, bounds.height / 2);
      context.textAlign = 'start';
    }
    const byId = new Map(visible.map(node => [node.id, node]));
    context.strokeStyle = COLORS.edge; context.lineWidth = 1.5;
    visible.forEach(node => { const parent = byId.get(node.parentId); if (!parent) return; context.globalAlpha = Math.min(node.opacity, parent.opacity); context.beginPath(); context.moveTo(parent.x, parent.y); context.lineTo(node.x, node.y); context.stroke(); });
    visible.forEach(node => {
      const completedAt = Date.parse(node.completedAt || '');
      const terminalFade = Number.isFinite(completedAt) ? Math.max(.35, 1 - ((Date.now() - completedAt) / 4_000)) : 1;
      context.globalAlpha = node.opacity * terminalFade;
      context.fillStyle = node.color;
      const running = node.kind === 'fiber' && node.state.includes('running');
      const pulse = running ? 1.5 + Math.sin(Date.now() / 220) * 1.5 : 0;
      context.beginPath(); context.arc(node.x, node.y, (node.kind === 'scope' ? 16 : 11) + pulse, 0, Math.PI * 2); context.fill();
      if (node.id === selected) { context.strokeStyle = COLORS.selected; context.lineWidth = 3; context.beginPath(); context.arc(node.x, node.y, node.kind === 'scope' ? 21 : 16, 0, Math.PI * 2); context.stroke(); }
      context.fillStyle = COLORS.text; context.globalAlpha = node.opacity * terminalFade; context.font = node.kind === 'scope' ? '700 13px ui-sans-serif, system-ui' : '12px ui-sans-serif, system-ui';
      const label = node.kind === 'fiber' ? `${node.name} · ${node.state}` : node.name;
      context.fillText(label, node.x + 24, node.y + 4);
    });
    context.restore(); context.globalAlpha = 1;
  };
  const animate = () => {
    let active = false;
    for (const [id, node] of nodes) {
      node.opacity += (node.targetOpacity - node.opacity) * .22;
      node.x += (node.targetX - node.x) * .22; node.y += (node.targetY - node.y) * .22;
      if (Math.abs(node.targetOpacity - node.opacity) > .01 || Math.abs(node.targetX - node.x) > .5 || Math.abs(node.targetY - node.y) > .5) active = true;
      if (node.targetOpacity < .01 && node.opacity < .015) nodes.delete(id);
    }
    draw(); if (active) frame = requestFrame(animate);
  };
  const fit = items => {
    if (!items.length) return;
    const bounds = canvas.getBoundingClientRect();
    const minimumX = Math.min(...items.map(node => node.x - 24));
    const maximumX = Math.max(...items.map(node => node.x + 180));
    const minimumY = Math.min(...items.map(node => node.y - 24));
    const maximumY = Math.max(...items.map(node => node.y + 28));
    const horizontal = Math.max(1, maximumX - minimumX);
    const vertical = Math.max(1, maximumY - minimumY);
    const availableWidth = Math.max(1, bounds.width - 32);
    const availableHeight = Math.max(1, bounds.height - 32);
    view.zoom = Math.min(2.5, Math.max(.08, Math.min(availableWidth / horizontal, availableHeight / vertical)));
    view.x = 16 - minimumX * view.zoom;
    view.y = 16 - minimumY * view.zoom;
  };
  const update = snapshot => {
    latestSnapshot = snapshot;
    const next = buildForestNodes(snapshot, { taskOnly }); const nextIds = new Set(next.map(node => node.id));
    emptyMessage = next.length ? '' : 'No logical scopes are visible in this snapshot.';
    if (!hasFitted && next.length) { fit(next); hasFitted = true; }
    next.forEach(item => {
      const existing = nodes.get(item.id);
      nodes.set(item.id, existing ? { ...existing, ...item, targetX: item.x, targetY: item.y, targetOpacity: 1 } : { ...item, x: item.x - 28, y: item.y, opacity: 0, targetX: item.x, targetY: item.y, targetOpacity: 1 });
    });
    nodes.forEach((node, id) => { if (!nextIds.has(id)) node.targetOpacity = 0; });
    if (!frame) frame = requestFrame(animate);
  };
  const resetView = () => { view = { x: 0, y: 0, zoom: 1 }; draw(); };
  const fitView = () => {
    if (!latestSnapshot) return;
    const items = buildForestNodes(latestSnapshot, { taskOnly });
    fit(items); hasFitted = true; draw();
  };
  const setTaskOnly = enabled => {
    taskOnly = enabled;
    hasFitted = false;
    if (latestSnapshot) update(latestSnapshot);
  };
  const wheel = event => { event.preventDefault(); const before = canvasPoint(event); const multiplier = event.deltaY < 0 ? 1.12 : .89; view.zoom = Math.min(2.5, Math.max(.08, view.zoom * multiplier)); const bounds = canvas.getBoundingClientRect(); view.x = event.clientX - bounds.left - before.x * view.zoom; view.y = event.clientY - bounds.top - before.y * view.zoom; draw(); };
  const pointerDown = event => { dragging = { x: event.clientX, y: event.clientY, startX: event.clientX, startY: event.clientY }; canvas.setPointerCapture?.(event.pointerId); };
  const pointerMove = event => { if (!dragging) return; view.x += event.clientX - dragging.x; view.y += event.clientY - dragging.y; dragging = { ...dragging, x: event.clientX, y: event.clientY }; draw(); };
  const pointerUp = event => {
    if (!dragging) return; const moved = Math.abs(event.clientX - dragging.startX) + Math.abs(event.clientY - dragging.startY); dragging = undefined;
    if (moved > 4) return;
    const point = canvasPoint(event); const hit = [...nodes.values()].filter(node => node.opacity > .5).find(node => Math.hypot(point.x - node.x, point.y - node.y) < 22);
    selected = hit?.id; onNodeClick(hit);
    draw();
  };
  const observer = globalThis.ResizeObserver ? new ResizeObserver(resize) : undefined; observer?.observe(canvas); resize();
  canvas.addEventListener('wheel', wheel, { passive: false }); canvas.addEventListener('pointerdown', pointerDown); canvas.addEventListener('pointermove', pointerMove); canvas.addEventListener('pointerup', pointerUp); canvas.addEventListener('pointercancel', pointerUp);
  return { update, resetView, fitView, setTaskOnly, destroy() { observer?.disconnect(); if (frame) cancelFrame(frame); canvas.removeEventListener('wheel', wheel); canvas.removeEventListener('pointerdown', pointerDown); canvas.removeEventListener('pointermove', pointerMove); canvas.removeEventListener('pointerup', pointerUp); canvas.removeEventListener('pointercancel', pointerUp); } };
}

function loadSnapshotHistory(storage) {
  try {
    const snapshots = JSON.parse(storage?.getItem(HISTORY_KEY) || '[]');
    return Array.isArray(snapshots) ? snapshots.filter(snapshot => Number.isFinite(snapshot?.version) && Array.isArray(snapshot?.roots)).slice(-HISTORY_LIMIT) : [];
  } catch { return []; }
}

function saveSnapshotHistory(storage, snapshots) {
  try { storage?.setItem(HISTORY_KEY, JSON.stringify(snapshots)); } catch { /* Local storage is optional. */ }
}

function snapshotTime(snapshot) {
  const date = new Date(snapshot.observedAt);
  return Number.isFinite(date.valueOf()) ? date.toLocaleTimeString() : 'time unavailable';
}

function snapshotNodeCount(snapshot) {
  const countScope = scope => 1 + (scope.fibers || []).length + (scope.scopes || []).reduce((total, child) => total + countScope(child), 0);
  return (snapshot.roots || []).reduce((total, root) => total + countScope(root), 0);
}

function snapshotSeverity(snapshot) {
  let severity = 'normal';
  const rank = { normal: 0, cancelled: 1, faulted: 2 };
  const observe = fiber => {
    const state = fiberState(fiber);
    const candidate = state.includes('fault') || fiber.errorCategory ? 'faulted' : state.includes('cancel') ? 'cancelled' : 'normal';
    if (rank[candidate] > rank[severity]) severity = candidate;
  };
  const visit = scope => { (scope.fibers || []).forEach(observe); (scope.scopes || []).forEach(visit); };
  (snapshot.roots || []).forEach(visit);
  return severity;
}

function renderSnapshotHistory(container, snapshots, selectedVersion, onLive, onSnapshot) {
  if (!container) return;
  const document = container.ownerDocument;
  const visible = snapshots.slice(-24);
  const maximumActivity = Math.max(1, ...visible.map(snapshotNodeCount));
  const live = document.createElement('button'); live.type = 'button'; live.className = 'fiber-history__live'; live.textContent = 'Live'; live.setAttribute('aria-current', String(selectedVersion == null)); live.addEventListener('click', onLive);
  const buttons = visible.map(snapshot => {
    const activity = snapshotNodeCount(snapshot);
    const severity = snapshotSeverity(snapshot);
    const description = `Snapshot ${snapshot.version} at ${snapshotTime(snapshot)} with ${activity} visible nodes`;
    const button = document.createElement('button'); button.type = 'button'; button.className = 'fiber-history__snapshot'; button.setAttribute('aria-current', String(snapshot.version === selectedVersion)); button.setAttribute('aria-label', description); button.title = description;
    const bar = document.createElement('span'); bar.className = 'fiber-history__bar'; bar.style?.setProperty('--activity', `${Math.max(12, Math.round(activity / maximumActivity * 100))}%`);
    const label = document.createElement('span'); label.className = `fiber-history__label fiber-history__label--${severity}`; label.textContent = snapshotTime(snapshot);
    button.append(bar, label); button.addEventListener('click', () => onSnapshot(snapshot)); return button;
  });
  container.replaceChildren(live, ...buttons);
}

function renderSimulationControls(container, scenarios, selectedId, message, onSelect, onRun) {
  if (!container) return;
  container.hidden = false;
  const document = container.ownerDocument;
  const title = document.createElement('strong'); title.textContent = 'Run a simulated registration';
  const controls = document.createElement('div'); controls.className = 'fiber-simulations__controls';
  const select = document.createElement('select');
  scenarios.forEach(scenario => { const option = document.createElement('option'); option.value = scenario.id; option.textContent = `${scenario.name} · ${scenario.expectedOutcome}`; option.selected = scenario.id === selectedId; select.append(option); });
  select.addEventListener('change', () => onSelect(select.value));
  const run = document.createElement('button'); run.type = 'button'; run.textContent = 'Run'; run.disabled = !selectedId; run.addEventListener('click', onRun);
  controls.append(select, run);
  const status = document.createElement('span'); status.className = 'fiber-simulations__status'; status.textContent = message;
  container.replaceChildren(title, controls, status);
}

export function createFiberDiagnosticsOverlay({ overlay, tree, toggle, close, reset, fit, taskOnly, pause, status, summary, history, simulations, selection, treeDetail, treeDetailBody, treeDetailClose, storage = globalThis.sessionStorage, fetchImpl = globalThis.fetch, EventSourceType = globalThis.EventSource, pageLifecycle = globalThis, streamUrl = '/_netcats/fibers/stream', simulationScenariosUrl = '/_netcats/fiber-simulations/scenarios' }) {
  let events; let simulationScenarios; let selectedSimulationId; let simulationMessage = ''; let liveSnapshot; let displayedSnapshot; let selectedHistoryVersion; let isTaskOnly = false; let isPaused = false;
  const snapshots = loadSnapshotHistory(storage);
  const documentBody = overlay.ownerDocument?.body;
  const forest = createForestRenderer(tree, hit => {
    selection.textContent = hit ? `${hit.kind === 'scope' ? 'Scope' : 'Fiber'}: ${hit.name} · ${hit.state}${hit.error ? ` · ${hit.error}` : ''}` : '';
    if (!hit || !displayedSnapshot || !treeDetail || !treeDetailBody) return;
    renderLogicalRuntimeTree(treeDetailBody, displayedSnapshot); treeDetail.hidden = false;
  });
  const setStatus = (text, state) => { status.textContent = text; status.dataset.state = state; };
  const renderSnapshot = (snapshot, historical = false) => {
    displayedSnapshot = snapshot; forest.update(snapshot);
    const mode = historical ? 'Historical' : isPaused ? 'Paused' : 'Live';
    summary.textContent = `${mode} snapshot ${snapshot.version} · ${snapshotTime(snapshot)} · scroll to zoom, drag to pan${snapshot.isTruncated ? ' · truncated' : ''}`;
    renderSnapshotHistory(history, snapshots, selectedHistoryVersion, showLive, showHistorical);
  };
  const showLive = () => {
    selectedHistoryVersion = undefined;
    if (isPaused) { isPaused = false; pause?.setAttribute('aria-pressed', 'false'); if (pause) pause.textContent = 'Pause'; }
    if (liveSnapshot) renderSnapshot(liveSnapshot);
    renderSimulationControls(simulations, simulationScenarios || [], selectedSimulationId, simulationMessage, selectSimulation, runSimulation);
  };
  const showHistorical = snapshot => { selectedHistoryVersion = snapshot.version; renderSnapshot(snapshot, true); };
  const receiveSnapshot = event => {
    const snapshot = JSON.parse(event.data); liveSnapshot = snapshot;
    if (!snapshots.length || snapshots[snapshots.length - 1].version !== snapshot.version)
    {
      snapshots.push(snapshot); while (snapshots.length > HISTORY_LIMIT) snapshots.shift(); saveSnapshotHistory(storage, snapshots);
    }
    if (!isPaused && selectedHistoryVersion == null) renderSnapshot(snapshot);
    else if (!isPaused) renderSnapshotHistory(history, snapshots, selectedHistoryVersion, showLive, showHistorical);
    setStatus('Connected', 'connected');
  };
  const selectSimulation = id => {
    selectedSimulationId = id;
    renderSimulationControls(simulations, simulationScenarios || [], selectedSimulationId, simulationMessage, selectSimulation, runSimulation);
  };
  const loadSimulationScenarios = async () => {
    if (!simulations || typeof fetchImpl !== 'function') return;
    try {
      const response = await fetchImpl(simulationScenariosUrl);
      if (!response.ok) { simulations.hidden = true; return; }
      simulationScenarios = await response.json();
      selectedSimulationId ||= simulationScenarios[0]?.id;
      if (!isPaused) renderSimulationControls(simulations, simulationScenarios, selectedSimulationId, simulationMessage, selectSimulation, runSimulation);
    } catch { simulations.hidden = true; }
  };
  const runSimulation = async () => {
    if (!selectedSimulationId || typeof fetchImpl !== 'function') return;
    const response = await fetchImpl(`${simulationScenariosUrl}/${encodeURIComponent(selectedSimulationId)}`, { method: 'POST' });
    simulationMessage = response.ok ? 'Simulation started; inspect its branch in the forest.' : 'That simulation is already running.';
    if (!isPaused) renderSimulationControls(simulations, simulationScenarios || [], selectedSimulationId, simulationMessage, selectSimulation, runSimulation);
  };
  const open = () => { overlay.hidden = false; documentBody?.classList?.add('fiber-forest-open'); toggle.setAttribute('aria-expanded', 'true'); void loadSimulationScenarios(); if (events) return; setStatus('Connecting…', 'connecting'); events = new EventSourceType(streamUrl); events.addEventListener('snapshot', receiveSnapshot); events.addEventListener('open', () => setStatus('Connected', 'connected')); events.addEventListener('error', () => setStatus(events?.readyState === EventSourceType.CLOSED ? 'Disconnected' : 'Reconnecting…', 'disconnected')); };
  const stop = ({ hide = true } = {}) => { if (events) { events.close(); events = undefined; } if (hide) overlay.hidden = true; documentBody?.classList?.remove('fiber-forest-open'); toggle.setAttribute('aria-expanded', 'false'); setStatus('Closed', 'closed'); };
  const pageHidden = () => stop();
  const closeTreeDetail = () => { if (treeDetail) treeDetail.hidden = true; };
  const toggleTaskOnly = () => { isTaskOnly = !isTaskOnly; taskOnly?.setAttribute('aria-pressed', String(isTaskOnly)); forest.setTaskOnly(isTaskOnly); };
  const togglePause = () => {
    isPaused = !isPaused;
    pause?.setAttribute('aria-pressed', String(isPaused));
    if (pause) pause.textContent = isPaused ? 'Resume' : 'Pause';
    if (isPaused && displayedSnapshot) renderSnapshot(displayedSnapshot, selectedHistoryVersion != null);
    if (!isPaused) showLive();
  };
  renderSnapshotHistory(history, snapshots, selectedHistoryVersion, showLive, showHistorical);
  toggle.setAttribute('aria-expanded', 'false'); toggle.addEventListener('click', open); close.addEventListener('click', stop); reset?.addEventListener('click', forest.resetView); fit?.addEventListener('click', forest.fitView); taskOnly?.addEventListener('click', toggleTaskOnly); pause?.addEventListener('click', togglePause); treeDetailClose?.addEventListener('click', closeTreeDetail); pageLifecycle.addEventListener('pagehide', pageHidden);
  return { open, close: stop, destroy() { stop(); forest.destroy(); toggle.removeEventListener('click', open); close.removeEventListener('click', stop); reset?.removeEventListener('click', forest.resetView); fit?.removeEventListener('click', forest.fitView); taskOnly?.removeEventListener('click', toggleTaskOnly); pause?.removeEventListener('click', togglePause); treeDetailClose?.removeEventListener('click', closeTreeDetail); pageLifecycle.removeEventListener('pagehide', pageHidden); }, isStreaming: () => Boolean(events) };
}
