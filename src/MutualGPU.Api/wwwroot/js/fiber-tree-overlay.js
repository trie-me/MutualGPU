function duration(startedAt, completedAt, observedAt) {
  const start = Date.parse(startedAt);
  const end = Date.parse(completedAt || observedAt);
  if (!Number.isFinite(start) || !Number.isFinite(end)) return 'duration unavailable';
  const milliseconds = Math.max(0, end - start);
  return milliseconds < 1000 ? `${milliseconds} ms` : `${(milliseconds / 1000).toFixed(1)} s`;
}

function stateName(fiber) {
  return fiber.outcome || fiber.state || 'Unknown';
}

function renderFiber(document, fiber, observedAt) {
  const element = document.createElement('div');
  const state = stateName(fiber);
  element.className = `fiber-node fiber-node--${String(state).toLowerCase()}`;
  const name = document.createElement('strong');
  name.textContent = fiber.name;
  const details = document.createElement('span');
  const started = new Date(fiber.startedAt).toLocaleTimeString();
  details.textContent = ` ${state} · ${started} · ${duration(fiber.startedAt, fiber.completedAt, observedAt)}`;
  element.append(name, details);
  if (fiber.errorCategory) {
    const error = document.createElement('span');
    error.className = 'fiber-node__error';
    error.textContent = ` · ${fiber.errorCategory}`;
    element.append(error);
  }
  return element;
}

function renderScope(document, scope, observedAt) {
  const element = document.createElement('details');
  element.className = `fiber-scope fiber-scope--${String(scope.state).toLowerCase()}`;
  element.open = true;
  const heading = document.createElement('summary');
  heading.textContent = `${scope.name} · ${scope.state}`;
  element.append(heading);
  for (const fiber of scope.fibers || []) element.append(renderFiber(document, fiber, observedAt));
  for (const child of scope.scopes || []) element.append(renderScope(document, child, observedAt));
  return element;
}

export function renderFiberSnapshot(tree, summary, snapshot) {
  const document = tree.ownerDocument;
  const nodes = (snapshot.roots || []).map(scope => renderScope(document, scope, snapshot.observedAt));
  if (!nodes.length) {
    const empty = document.createElement('p');
    empty.className = 'fiber-tree__empty';
    empty.textContent = 'No logical scopes are currently visible.';
    nodes.push(empty);
  }
  if (snapshot.isTruncated) {
    const warning = document.createElement('p');
    warning.className = 'fiber-tree__truncated';
    warning.textContent = 'Some nodes are omitted by the diagnostics capacity limit.';
    nodes.push(warning);
  }
  tree.replaceChildren(...nodes);
  summary.textContent = `Snapshot ${snapshot.version} · ${new Date(snapshot.observedAt).toLocaleTimeString()}${snapshot.isTruncated ? ' · truncated' : ''}`;
}

export function createFiberDiagnosticsOverlay({
  overlay,
  tree,
  toggle,
  close,
  status,
  summary,
  EventSourceType = globalThis.EventSource,
  pageLifecycle = globalThis,
  streamUrl = '/_netcats/fibers/stream',
}) {
  let events;

  const setStatus = (text, state) => {
    status.textContent = text;
    status.dataset.state = state;
  };
  const receiveSnapshot = event => {
    renderFiberSnapshot(tree, summary, JSON.parse(event.data));
    setStatus('Connected', 'connected');
  };
  const opened = () => setStatus('Connected', 'connected');
  const failed = () => setStatus(events?.readyState === EventSourceType.CLOSED ? 'Disconnected' : 'Reconnecting…', 'disconnected');

  const open = () => {
    overlay.hidden = false;
    toggle.setAttribute('aria-expanded', 'true');
    if (events) return;
    setStatus('Connecting…', 'connecting');
    events = new EventSourceType(streamUrl);
    events.addEventListener('snapshot', receiveSnapshot);
    events.addEventListener('open', opened);
    events.addEventListener('error', failed);
  };
  const stop = ({ hide = true } = {}) => {
    if (events) {
      events.removeEventListener('snapshot', receiveSnapshot);
      events.removeEventListener('open', opened);
      events.removeEventListener('error', failed);
      events.close();
      events = undefined;
    }
    if (hide) overlay.hidden = true;
    toggle.setAttribute('aria-expanded', 'false');
    setStatus('Closed', 'closed');
  };
  const pageHidden = () => stop();

  toggle.setAttribute('aria-expanded', 'false');
  toggle.addEventListener('click', open);
  close.addEventListener('click', stop);
  pageLifecycle.addEventListener('pagehide', pageHidden);

  return {
    open,
    close: stop,
    destroy() {
      stop();
      toggle.removeEventListener('click', open);
      close.removeEventListener('click', stop);
      pageLifecycle.removeEventListener('pagehide', pageHidden);
    },
    isStreaming: () => Boolean(events),
  };
}
