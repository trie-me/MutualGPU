export function renderTaskList(container, tasks, {
  fetchImpl = globalThis.fetch,
  openWindow = globalThis.open,
  onChanged = async () => {},
} = {}) {
  const document = container.ownerDocument;
  container.replaceChildren();
  if (!tasks.length) {
    container.textContent = 'No tasks yet.';
    return;
  }

  for (const task of tasks) {
    const article = document.createElement('article');
    article.className = 'task-list__item';
    const title = document.createElement('button'); title.type = 'button'; title.className = 'task-list__title';
    title.textContent = `${task.capabilityName}: ${task.status}`;
    const details = document.createElement('div'); details.className = 'task-list__details'; details.hidden = task.status !== 'Running';
    title.onclick = () => { details.hidden = !details.hidden; title.setAttribute('aria-expanded', String(!details.hidden)); };
    title.setAttribute('aria-expanded', String(!details.hidden));
    const attempt = document.createElement('span'); attempt.textContent = `${task.attemptCount} attempt(s)`;
    const resources = document.createElement('span'); resources.textContent = `${task.resources?.computeTier || 'Unspecified'} compute · ${task.resources?.memoryGiB || '?'} GiB`;
    details.append(attempt, resources);
    if (task.progress) {
      const progress = document.createElement('span'); progress.textContent = `${task.progress.phase || 'Working'} · ${task.progress.percent ?? 0}%${task.progress.message ? ` · ${task.progress.message}` : ''}`;
      details.append(progress);
    }
    if (task.failureStep) { const failure = document.createElement('span'); failure.className = 'task-list__failure'; failure.textContent = `Failure: ${task.failureStep}`; details.append(failure); }
    const actions = document.createElement('div'); actions.className = 'task-list__actions';
    if (task.canReevaluate) {
      const action = document.createElement('button');
      action.textContent = 'Reevaluate';
      action.onclick = async () => { const response = await fetchImpl(`/api/tasks/${task.taskId}/reevaluate`, { method: 'POST' }); if (response.ok) await onChanged(); };
      actions.append(action);
    }
    if (task.canRetrieveResult) {
      const result = document.createElement('a');
      result.textContent = 'Result';
      result.href = `/api/tasks/${task.taskId}/result`;
      result.onclick = async event => {
        event.preventDefault();
        const descriptor = await fetchImpl(result.href).then(value => value.json());
        const download = descriptor.artifacts?.[0]?.downloadUrl;
        if (download) openWindow(download, '_blank', 'noopener');
      };
      actions.append(result);
    }
    article.append(title, details, actions);
    container.append(article);
  }
}
