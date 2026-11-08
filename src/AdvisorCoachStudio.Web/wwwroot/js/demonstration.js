import { apiFetch, showMessage } from './site.js';
import { RealtimeSessionClient } from './realtime-client.js';

const STATE_TEXT = {
  connecting: 'Connecting\u2026',
  ready: 'Starting the demonstration\u2026',
  client_speaking: 'Demonstration playing',
  listening: 'Demonstration playing',
  ending: 'Stopping\u2026',
  ended: 'Demonstration finished',
  failed: 'Demonstration could not start',
};

let initialized = false;

/** Output-only AI-as-advisor demonstration. Never requests the microphone and never affects the report. */
export function initDemonstration(sessionId) {
  if (initialized) return;
  initialized = true;
  const startButton = document.getElementById('demo-start');
  const stopButton = document.getElementById('demo-stop');
  const stateEl = document.getElementById('demo-state');
  const errorEl = document.getElementById('demo-error');
  const transcriptEl = document.getElementById('demo-transcript');
  let client = null;
  let audioContext = null;

  const setState = (state) => { stateEl.textContent = STATE_TEXT[state] ?? state; };

  const renderTurn = ({ turnId, text, final }) => {
    let paragraph = transcriptEl.querySelector(`[data-turn-id="${CSS.escape(String(turnId))}"]`);
    if (!paragraph) {
      transcriptEl.querySelectorAll('.hint').forEach((hint) => hint.remove());
      paragraph = document.createElement('p');
      paragraph.dataset.turnId = String(turnId);
      transcriptEl.append(paragraph);
    }
    paragraph.textContent = text;
    paragraph.classList.toggle('provisional', !final);
  };

  const reset = () => {
    stopButton.disabled = true;
    startButton.disabled = false;
    startButton.textContent = 'Play a new demonstration';
    audioContext?.close().catch(() => {});
    audioContext = null;
    client = null;
  };

  startButton.addEventListener('click', async () => {
    startButton.disabled = true;
    showMessage(errorEl, '');
    transcriptEl.replaceChildren();
    try {
      audioContext = new AudioContext({ latencyHint: 'interactive' });
      await audioContext.resume();
      const created = await apiFetch(`/api/sessions/${sessionId}/demonstrations`, { method: 'POST' });
      client = new RealtimeSessionClient({ url: created.webSocketPath, mode: 'demonstration', audioContext });
      client.addEventListener('state', (event) => setState(event.detail.state));
      client.addEventListener('transcript', (event) => renderTurn(event.detail));
      client.addEventListener('error', (event) => showMessage(errorEl, event.detail.message));
      client.addEventListener('ended', (event) => {
        setState(event.detail?.reason === 'completed' ? 'ended' : 'ended');
        reset();
      });
      stopButton.disabled = false;
      await client.start();
    } catch (error) {
      setState('failed');
      showMessage(errorEl, error?.message ?? 'The demonstration could not start.');
      reset();
    }
  });

  stopButton.addEventListener('click', () => {
    stopButton.disabled = true;
    client?.end('user');
  });
}
