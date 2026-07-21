const statusCopy = Object.freeze({
  Queued: { label: 'Finding a provider', description: 'Your task has a place in the queue.' },
  Assigned: { label: 'Provider preparing', description: 'A compatible provider is getting ready to accept the work.' },
  Running: { label: 'Making progress', description: 'Small machines, serious business.' },
  Completed: { label: 'Result ready', description: 'Fresh from the GPU.' },
  Failed: { label: 'Needs attention', description: 'This task needs a human to look at the details.' },
});

function presentationFor(status) {
  return statusCopy[status] || { label: status || 'Working', description: 'MutualGPU is keeping track of this task.' };
}

function statusClass(status) {
  return String(status || 'unknown').toLowerCase();
}

function failureMessage(task) {
  if (task.failureReason?.trim()) return task.failureReason.trim();

  // Older task records predate failureReason and stored their only explanation in
  // failureStep. Keep those records useful while new records separate code from copy.
  return {
    acknowledgement_timeout: 'The provider did not accept the task before the acknowledgement deadline.',
    delivery_failed: 'The provider connection closed before the task could be delivered.',
    disconnect_recovery_expired: 'The provider disconnected and did not reconnect before the recovery window expired.',
    restart_recovery: 'The service restarted while this task was active.',
    result_validation: 'The provider result could not be validated.',
  }[task.failureStep] || task.failureStep || 'No additional failure details were supplied.';
}

export function renderTaskList(container, tasks, {
  fetchImpl = globalThis.fetch,
  openWindow = globalThis.open,
  onChanged = async () => {},
} = {}) {
  const document = container.ownerDocument;
  container.replaceChildren();
  if (!tasks.length) {
    const empty = document.createElement('div'); empty.className = 'task-list__empty';
    const title = document.createElement('strong'); title.textContent = 'Nothing on the reading list yet.';
    const copy = document.createElement('span'); copy.textContent = 'Create a task and its matching, progress, and result will appear here.';
    empty.append(title, copy); container.append(empty);
    return;
  }

  for (const task of tasks) {
    const presentation = presentationFor(task.status);
    const article = document.createElement('article'); article.className = 'task-list__item';
    const title = document.createElement('button'); title.type = 'button'; title.className = 'task-list__title';
    const titleCopy = document.createElement('span'); titleCopy.className = 'task-list__title-copy';
    const name = document.createElement('strong'); name.textContent = task.capabilityName;
    const summary = document.createElement('span'); summary.textContent = presentation.description;
    titleCopy.append(name, summary);
    const status = document.createElement('span'); status.className = `task-list__status task-list__status--${statusClass(task.status)}`; status.textContent = presentation.label;
    title.append(titleCopy, status);

    const details = document.createElement('div'); details.className = 'task-list__details'; details.hidden = task.status !== 'Running' && !task.failureStep && !task.failureReason;
    title.onclick = () => { details.hidden = !details.hidden; title.setAttribute('aria-expanded', String(!details.hidden)); };
    title.setAttribute('aria-expanded', String(!details.hidden));
    const attempt = document.createElement('span'); attempt.textContent = `${task.attemptCount} attempt${task.attemptCount === 1 ? '' : 's'} recorded`;
    const resources = document.createElement('span'); resources.textContent = `${task.resources?.computeTier || 'Unspecified'} compute · ${task.resources?.memoryGiB || '?'} GiB unified memory`;
    details.append(attempt, resources);
    if (task.progress) {
      const progress = document.createElement('span'); progress.textContent = `${task.progress.phase || 'Working'} · ${task.progress.percent ?? 0}%${task.progress.message ? ` · ${task.progress.message}` : ''}`;
      const meter = document.createElement('div'); meter.className = 'task-list__progress';
      const fill = document.createElement('span'); fill.setAttribute('style', `width:${Math.max(0, Math.min(100, task.progress.percent ?? 0))}%`);
      meter.append(fill); details.append(progress, meter);
    }
    if (task.failureStep || task.failureReason) { const failure = document.createElement('span'); failure.className = 'task-list__failure'; failure.textContent = `Needs attention: ${failureMessage(task)}`; details.append(failure); }
    const actions = document.createElement('div'); actions.className = 'task-list__actions';
    if (task.canReevaluate) {
      const action = document.createElement('button');
      action.textContent = 'Try matching again';
      action.onclick = async () => { const response = await fetchImpl(`/api/tasks/${task.taskId}/reevaluate`, { method: 'POST' }); if (response.ok) await onChanged(); };
      actions.append(action);
    }
    if (task.canRetrieveResult) {
      const result = document.createElement('a');
      result.textContent = 'Download result';
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
