const form = document.getElementById('partner-resource-form');

if (form) {
  const status = document.getElementById('partner-resource-status');
  const button = form.querySelector('button[type="submit"]');

  form.addEventListener('submit', async event => {
    event.preventDefault();
    status.textContent = '';
    button.disabled = true;
    const data = new FormData(form);
    try {
      const response = await fetch('/api/partner-resources', {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(Object.fromEntries(data))
      });
      if (!response.ok) {
        const problem = await response.json().catch(() => ({}));
        const detail = Object.values(problem.errors || {}).flat()[0];
        status.textContent = detail || problem.title || 'We could not send this request. Please check the details and try again.';
        status.className = 'form-error';
        return;
      }
      form.reset();
      status.textContent = 'Request received. An administrator will vet the resource before approving it.';
      status.className = 'form-success';
    } catch {
      status.textContent = 'We could not reach MutualGPU. Please try again.';
      status.className = 'form-error';
    } finally {
      button.disabled = false;
    }
  });
}
