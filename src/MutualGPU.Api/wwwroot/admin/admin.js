const el = id => document.getElementById(id);
const state = { data: null, partnerResources: [], selectedSession: null, view: 'sessions', query: '' };

el('login-form').addEventListener('submit', async event => {
  event.preventDefault();
  const error = el('login-error');
  error.hidden = true;
  const response = await fetch('/admin/api/login', {
    method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ password: el('password').value })
  });
  el('password').value = '';
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}));
    error.textContent = problem.title || 'Administrator login failed.';
    error.hidden = false;
    return;
  }
  await load();
});

el('logout').addEventListener('click', async () => {
  await fetch('/admin/api/logout', { method: 'POST', credentials: 'same-origin' });
  showLogin();
});
el('refresh').addEventListener('click', load);
el('search').addEventListener('input', event => { state.query = event.target.value.toLowerCase().trim(); render(); });
document.querySelectorAll('.tab').forEach(button => button.addEventListener('click', () => {
  state.view = button.dataset.view;
  document.querySelectorAll('.tab').forEach(item => item.classList.toggle('active', item === button));
  render();
}));

async function load() {
  const [response, resources] = await Promise.all([
    fetch('/admin/api/overview', { credentials: 'same-origin', cache: 'no-store' }),
    fetch('/admin/api/partner-resources/pending', { credentials: 'same-origin', cache: 'no-store' })
  ]);
  if (response.status === 401 || resources.status === 401) { showLogin(); return; }
  if (!response.ok || !resources.ok) { el('live-state').textContent = 'Unavailable'; return; }
  state.data = await response.json();
  state.partnerResources = await resources.json();
  el('login-view').hidden = true;
  el('console-view').hidden = false;
  render();
}

function showLogin() {
  state.data = null;
  state.partnerResources = [];
  el('console-view').hidden = true;
  el('login-view').hidden = false;
  el('password').focus();
}

function render() {
  if (!state.data) return;
  const { summary } = state.data;
  el('updated-at').textContent = `Updated ${formatTime(state.data.generatedAt)}`;
  const metrics = [
    ['Connected', summary.connectedSessions], ['Sessions retained', summary.retainedSessions],
    ['Transactions', summary.transactions], ['Running', summary.runningTransactions],
    ['Assignments', summary.assignments], ['Unsuccessful', summary.unsuccessfulAssignments]
  ];
  replace(el('metrics'), metrics.map(([label, value]) => node('div', 'metric', node('strong', '', String(value)), node('span', '', label))));
  ['sessions', 'transactions', 'assignments', 'partner-resources'].forEach(view => { el(`${view}-view`).hidden = state.view !== view; });
  renderSessions(); renderTransactions(); renderAssignments(); renderPartnerResources();
}

function renderSessions() {
  const sessions = state.data.sessions.filter(item => matches(item.sessionId, item.executionUnitId, item.providerName, item.transport, item.sourceIp, item.status));
  const providers = groupProviderSessions(sessions);
  el('session-count').textContent = `${providers.length} provider${providers.length === 1 ? '' : 's'} · ${sessions.length} session${sessions.length === 1 ? '' : 's'}`;
  replace(el('session-list'), providers.map(provider => {
    const session = provider.current;
    const assignmentCount = provider.sessions.reduce((total, item) => total + item.assignmentCount, 0);
    const reconnectCount = Math.max(0, provider.sessions.length - 1);
    const reconnectText = reconnectCount === 0 ? 'single connection' : `${reconnectCount} reconnect${reconnectCount === 1 ? '' : 's'}`;
    const selected = provider.sessions.some(item => item.sessionId === state.selectedSession);
    const button = node('button', `session-card${selected ? ' active' : ''}`,
      node('span', `status-dot ${session.status}`),
      node('span', '', node('strong', '', providerName(session)), node('small', '', `${shortId(session.executionUnitId)} · ${session.transport} · ${session.sourceIp || 'source unknown'} · ${assignmentCount} assignment${assignmentCount === 1 ? '' : 's'} · ${reconnectText}`)),
      node('time', '', age(session.connectedAt)));
    button.type = 'button';
    button.addEventListener('click', () => { state.selectedSession = session.sessionId; renderSessions(); });
    return button;
  }));
  const selected = state.data.sessions.find(item => item.sessionId === state.selectedSession);
  if (!selected) { replace(el('session-detail'), [node('p', 'empty', 'Select a session to inspect its summary and event timeline.')]); return; }
  const summary = selected.summary;
  const meta = node('div', 'detail-meta',
    metaItem('Provider', providerName(selected)), metaItem('Provider ID', selected.executionUnitId), metaItem('Transport', selected.transport),
    metaItem('Source IP', selected.sourceIp || 'Unknown'),
    metaItem('Connected', formatDate(selected.connectedAt)), metaItem('Duration', duration(selected.connectedAt, selected.closedAt)));
  const stats = node('div', 'mini-stats',
    ...[['Events', summary.eventCount], ['Assigned', summary.assigned], ['Accepted', summary.accepted], ['Completed', summary.completed], ['Failed', summary.failed], ['Rejected', summary.rejected], ['Progress', summary.progressUpdates]].map(([label, value]) => node('span', '', `${label} ${value}`)));
  const timeline = node('ol', 'timeline', ...selected.events.slice().reverse().map(event => node('li', '',
    node('strong', '', event.type.replaceAll('_', ' ')), node('small', '', event.summary),
    node('small', '', formatDate(event.occurredAt)), event.taskId ? node('code', '', `task ${shortId(event.taskId)} · attempt ${shortId(event.attemptId)}`) : null)));
  replace(el('session-detail'), [node('div', 'detail-title', node('div', '', node('p', 'eyebrow', 'Session detail'), node('h2', '', shortId(selected.sessionId))), pill(selected.status)), meta, stats, node('h3', '', 'Event timeline'), timeline]);
}

function groupProviderSessions(sessions) {
  const providers = new Map();
  for (const session of sessions) {
    const key = session.executionUnitId || session.sessionId;
    const provider = providers.get(key) || { sessions: [], current: null, latest: null };
    provider.sessions.push(session);
    if (!provider.latest || new Date(session.connectedAt) > new Date(provider.latest.connectedAt)) provider.latest = session;
    if (session.status === 'connected' && (!provider.current || new Date(session.connectedAt) > new Date(provider.current.connectedAt))) provider.current = session;
    providers.set(key, provider);
  }
  return [...providers.values()]
    .map(provider => ({ ...provider, current: provider.current || provider.latest }))
    .sort((left, right) => new Date(right.current.connectedAt) - new Date(left.current.connectedAt));
}

function renderTransactions() {
  const rows = state.data.transactions.filter(item => matches(item.taskId, item.requestorId, item.capability, item.status));
  el('transaction-count').textContent = `${rows.length} shown`;
  replace(el('transactions-body'), rows.map(item => node('tr', '',
    node('td', '', node('code', '', shortId(item.taskId)), node('small', '', `requestor ${shortId(item.requestorId)}`)),
    node('td', '', item.capability), node('td', '', pill(item.status)), node('td', '', `${item.computeTier} · ${item.memoryGiB} GiB`),
    node('td', '', String(item.attemptCount)), node('td', '', item.hasResult ? 'Available' : '—'), node('td', '', formatDate(item.createdAt)))));
}

function renderAssignments() {
  const rows = state.data.assignments.filter(item => matches(item.attemptId, item.taskId, item.executionUnitId, item.sessionId, providerNameFor(item.sessionId), sourceIpFor(item.sessionId), item.capability, item.state, item.failureStep, item.failureReason));
  el('assignment-count').textContent = `${rows.length} shown`;
  replace(el('assignments-body'), rows.map(item => {
    const session = node('button', 'quiet', item.sessionId ? shortId(item.sessionId) : 'not retained');
    session.disabled = !item.sessionId;
    if (item.sessionId) session.addEventListener('click', () => { state.selectedSession = item.sessionId; document.querySelector('[data-view="sessions"]').click(); });
    const transition = item.disconnectedAt || item.acceptedAt;
    const outcome = item.failureStep || item.failureReason
      ? node('span', '', item.failureStep || 'Provider detail', item.failureReason ? node('small', '', item.failureReason) : null)
      : '—';
    return node('tr', '', node('td', '', node('code', '', shortId(item.attemptId))), node('td', '', item.capability, node('small', '', shortId(item.taskId))),
      node('td', '', providerNameFor(item.sessionId) || shortId(item.executionUnitId), node('small', '', `${shortId(item.executionUnitId)} · ${sourceIpFor(item.sessionId) || 'source unknown'}`)), node('td', '', session), node('td', '', pill(item.state)), node('td', '', formatDate(item.assignedAt)),
      node('td', '', transition ? formatDate(transition) : '—'), node('td', '', outcome));
  }));
}

function renderPartnerResources() {
  const requests = state.partnerResources.filter(item => matches(item.partnerName, item.contactEmail, item.origin, item.id));
  el('partner-resource-count').textContent = `${requests.length} awaiting review`;
  replace(el('partner-resource-list'), requests.length ? requests.map(item => {
    const approve = node('button', 'approve-resource', 'Approve & whitelist');
    approve.type = 'button';
    approve.addEventListener('click', async () => {
      approve.disabled = true;
      const response = await fetch(`/admin/api/partner-resources/${encodeURIComponent(item.id)}/approve`, { method: 'POST', credentials: 'same-origin' });
      if (!response.ok) { approve.disabled = false; approve.textContent = 'Could not approve — retry'; return; }
      state.partnerResources = state.partnerResources.filter(request => request.id !== item.id);
      renderPartnerResources();
    });
    return node('article', 'partner-resource-card',
      node('div', '', node('p', 'eyebrow', 'Pending review'), node('h3', '', item.partnerName), node('p', 'resource-origin', item.origin), node('p', 'muted', item.contactEmail), node('small', '', `Submitted ${formatDate(item.submittedAt)}`)),
      approve);
  }) : [node('p', 'empty pending-empty', 'No partner resource requests are awaiting review.')]);
}

function node(tag, className, ...children) {
  const result = document.createElement(tag);
  if (className) result.className = className;
  children.flat().filter(child => child !== null && child !== undefined).forEach(child => result.append(child instanceof Node ? child : document.createTextNode(String(child))));
  return result;
}
function replace(parent, children) { parent.replaceChildren(...children); }
function pill(value) { return node('span', `pill ${value}`, value); }
function metaItem(label, value) { return node('div', '', node('span', '', label), node('strong', '', value)); }
function matches(...values) { return !state.query || values.some(value => String(value ?? '').toLowerCase().includes(state.query)); }
function providerName(session) { return session.providerName || shortId(session.executionUnitId); }
function providerNameFor(sessionId) { const session = state.data?.sessions.find(item => item.sessionId === sessionId); return session ? providerName(session) : ''; }
function sourceIpFor(sessionId) { return state.data?.sessions.find(session => session.sessionId === sessionId)?.sourceIp || ''; }
function shortId(value) {
  if (!value) return '—';
  const normalized = String(value).replaceAll('-', '');
  return normalized.length > 8 ? normalized.slice(-8) : normalized;
}
function formatDate(value) { return value ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'medium' }).format(new Date(value)) : '—'; }
function formatTime(value) { return new Intl.DateTimeFormat(undefined, { timeStyle: 'medium' }).format(new Date(value)); }
function age(value) { const seconds = Math.max(0, (Date.now() - new Date(value).getTime()) / 1000); if (seconds < 60) return `${Math.floor(seconds)}s`; if (seconds < 3600) return `${Math.floor(seconds / 60)}m`; if (seconds < 86400) return `${Math.floor(seconds / 3600)}h`; return `${Math.floor(seconds / 86400)}d`; }
function duration(start, end) { const seconds = Math.max(0, (new Date(end || Date.now()).getTime() - new Date(start).getTime()) / 1000); if (seconds < 60) return `${Math.floor(seconds)} sec`; if (seconds < 3600) return `${Math.floor(seconds / 60)} min`; return `${Math.floor(seconds / 3600)} hr ${Math.floor((seconds % 3600) / 60)} min`; }

load();
