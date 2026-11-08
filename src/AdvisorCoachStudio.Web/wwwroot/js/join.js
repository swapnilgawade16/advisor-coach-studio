import { microphoneErrorMessage, showMessage, supportsLiveAudio } from './site.js';
import { MICROPHONE_CONSTRAINTS } from './realtime-client.js';
import { runLiveSession } from './live-session.js';

// Joins a created-but-not-started session (for example after a reload of /practice/{sessionId}).
const root = document.getElementById('session-root');
const joinButton = document.getElementById('join-button');
const acknowledge = document.getElementById('acknowledge');
const errorEl = document.getElementById('setup-error');
const supported = supportsLiveAudio();
let joining = false;

if (!supported) document.getElementById('browser-support').hidden = false;

const update = () => { joinButton.disabled = joining || !supported || !acknowledge.checked; };
acknowledge.addEventListener('change', update);

joinButton.addEventListener('click', async () => {
  if (joinButton.disabled) return;
  joining = true;
  update();
  showMessage(errorEl, '');
  let audioContext;
  let stream;
  try {
    audioContext = new AudioContext({ latencyHint: 'interactive' });
    await audioContext.resume();
    stream = await navigator.mediaDevices.getUserMedia(MICROPHONE_CONSTRAINTS);
  } catch (error) {
    showMessage(errorEl, error?.name ? microphoneErrorMessage(error) : 'Audio could not be started in this browser.');
    audioContext?.close();
    joining = false;
    update();
    return;
  }

  document.getElementById('join').hidden = true;
  const sessionId = root.dataset.sessionId;
  const live = document.getElementById('live');
  await runLiveSession({
    sessionId,
    websocketPath: `/ws/sessions/${sessionId}`,
    reportUrl: `/sessions/${sessionId}/report`,
    maxSeconds: Number(live.dataset.maxSeconds) || 120,
    audioContext,
    mediaStream: stream,
  });
});

update();
