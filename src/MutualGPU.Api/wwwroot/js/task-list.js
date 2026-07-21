export function renderTaskList(container, tasks, {
  fetchImpl = globalThis.fetch,
  openWindow = globalThis.open,
} = {}) {
  const document = container.ownerDocument;
  container.replaceChildren();
  if (!tasks.length) {
    container.textContent = 'No tasks yet.';
    return;
  }

  for (const task of tasks) {
    const article = document.createElement('article');
    const title = document.createElement('strong');
    title.textContent = `${task.capabilityName}: ${task.status}`;
    article.append(title, document.createTextNode(` · ${task.attemptCount} attempt(s)`));
    if (task.failureStep) article.append(document.createTextNode(` · ${task.failureStep}`));
    if (task.canReevaluate) {
      const action = document.createElement('button');
      action.textContent = 'Reevaluate';
      action.onclick = () => fetchImpl(`/api/tasks/${task.taskId}/reevaluate`, { method: 'POST' });
      article.append(' ', action);
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
      article.append(' ', result);
    }
    container.append(article);
  }
}
