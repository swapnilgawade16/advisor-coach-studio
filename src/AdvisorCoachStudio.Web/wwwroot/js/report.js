import { apiFetch, prefersReducedMotion } from './site.js';

const root = document.getElementById('report-root');
const sessionId = root?.dataset.sessionId;

// Evidence links expand the transcript and move focus to the referenced turn.
document.addEventListener('click', (event) => {
  const link = event.target.closest?.('a.evidence-link');
  if (!link) return;
  const turn = document.getElementById(link.dataset.turn);
  if (!turn) return;
  event.preventDefault();
  const details = document.getElementById('report-transcript');
  if (details) details.open = true;
  document.querySelectorAll('.transcript li.highlight').forEach((item) => item.classList.remove('highlight'));
  turn.classList.add('highlight');
  turn.scrollIntoView({ block: 'center', behavior: prefersReducedMotion() ? 'auto' : 'smooth' });
  turn.focus({ preventScroll: true });
  history.replaceState(null, '', link.getAttribute('href'));
});

document.getElementById('discard-form')?.addEventListener('submit', (event) => {
  if (!window.confirm('Discard this session, its transcript, and its report? This cannot be undone.')) {
    event.preventDefault();
  }
});

// Poll while the conversation is finalizing or the report is queued/generating, then reload to render it.
if (root && sessionId && root.dataset.pending === 'true') {
  const initialStatus = root.dataset.reportStatus;
  const statusText = document.getElementById('report-status-text');
  const startedAt = Date.now();
  const poll = async () => {
    if (Date.now() - startedAt > 5 * 60 * 1000) {
      if (statusText) statusText.textContent = 'This is taking longer than expected. Reload the page to check again.';
      return;
    }
    try {
      const data = await apiFetch(`/api/sessions/${sessionId}/report`);
      if (data?.status && data.status !== initialStatus) {
        window.location.reload();
        return;
      }
      if (statusText && data?.message && statusText.textContent !== data.message) statusText.textContent = data.message;
    } catch (error) {
      if (error?.status === 404) {
        window.location.assign('/');
        return;
      }
    }
    window.setTimeout(poll, 2500);
  };
  window.setTimeout(poll, 2000);
}

// The demonstration controller loads on demand so the report page never touches audio unless asked.
document.getElementById('demo-open')?.addEventListener('click', async () => {
  const panel = document.getElementById('demo-panel');
  if (panel) {
    panel.hidden = false;
    panel.scrollIntoView({ block: 'start', behavior: prefersReducedMotion() ? 'auto' : 'smooth' });
    document.getElementById('demo-start')?.focus();
  }
  const module = await import('./demonstration.js');
  module.initDemonstration(sessionId);
}, { once: true });
