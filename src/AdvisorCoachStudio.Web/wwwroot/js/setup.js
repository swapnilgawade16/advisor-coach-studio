import { apiFetch, microphoneErrorMessage, showMessage, supportsLiveAudio } from './site.js';
import { MICROPHONE_CONSTRAINTS } from './realtime-client.js';
import { runLiveSession } from './live-session.js';

const root = document.getElementById('setup-root');
const form = document.getElementById('setup-form');
const micTest = document.getElementById('mic-test');
const micLevel = document.getElementById('mic-level');
const micStatus = document.getElementById('mic-status');
const acknowledge = document.getElementById('acknowledge');
const startButton = document.getElementById('start-button');
const setupError = document.getElementById('setup-error');

const serviceReady = root.dataset.ready === 'true';
const supported = supportsLiveAudio();
let audioContext = null;
let stream = null;
let meterSource = null;
let meterFrame = 0;
let microphoneOk = false;
let starting = false;

if (!supported) {
  document.getElementById('browser-support').hidden = false;
  micTest.disabled = true;
}

const updateStart = () => {
  startButton.disabled = starting || !(serviceReady && supported && microphoneOk && acknowledge.checked);
};

const stopMeter = () => {
  window.cancelAnimationFrame(meterFrame);
  meterFrame = 0;
  try { meterSource?.disconnect(); } catch { /* already disconnected */ }
  meterSource = null;
  micLevel.value = 0;
};

micTest.addEventListener('click', async () => {
  micTest.disabled = true;
  micStatus.textContent = 'Requesting microphone access\u2026';
  try {
    audioContext ??= new AudioContext({ latencyHint: 'interactive' });
    await audioContext.resume();
    stopMeter();
    stream?.getTracks().forEach((track) => track.stop());
    stream = await navigator.mediaDevices.getUserMedia(MICROPHONE_CONSTRAINTS);
    const analyser = audioContext.createAnalyser();
    analyser.fftSize = 1024;
    meterSource = audioContext.createMediaStreamSource(stream);
    meterSource.connect(analyser);
    const samples = new Float32Array(analyser.fftSize);
    const tick = () => {
      analyser.getFloatTimeDomainData(samples);
      let sum = 0;
      for (const sample of samples) sum += sample * sample;
      micLevel.value = Math.min(1, Math.sqrt(sum / samples.length) * 4);
      meterFrame = window.requestAnimationFrame(tick);
    };
    tick();
    microphoneOk = true;
    micStatus.textContent = 'Microphone is working. Speak to see the level move. This check stays on your device.';
  } catch (error) {
    microphoneOk = false;
    micStatus.textContent = microphoneErrorMessage(error);
  } finally {
    micTest.disabled = !supported;
    updateStart();
  }
});

acknowledge.addEventListener('change', updateStart);

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  if (startButton.disabled || starting) return;
  starting = true;
  updateStart();
  showMessage(setupError, '');
  const difficulty = new FormData(form).get('difficulty') ?? 'intermediate';
  try {
    // Resume inside the user's Start gesture so client speech can play (autoplay policy).
    await audioContext.resume();
    if (!stream?.getAudioTracks().some((track) => track.readyState === 'live')) {
      stream = await navigator.mediaDevices.getUserMedia(MICROPHONE_CONSTRAINTS);
    }
    const created = await apiFetch('/api/sessions', { method: 'POST', body: { scenarioId: root.dataset.scenarioId, difficulty, acknowledgedNotice: true } });
    stopMeter();
    document.getElementById('setup').hidden = true;
    history.replaceState(null, '', created.practiceUrl);
    const label = document.querySelector(`label[for="difficulty-${difficulty}"]`)?.textContent ?? difficulty;
    await runLiveSession({
      sessionId: created.sessionId,
      websocketPath: created.webSocketPath,
      reportUrl: created.reportUrl,
      maxSeconds: created.maxDurationSeconds,
      audioContext,
      mediaStream: stream,
      difficultyName: label,
    });
    document.getElementById('live-heading')?.focus?.();
  } catch (error) {
    showMessage(setupError, error?.message ?? 'The practice session could not be created.');
    starting = false;
    updateStart();
  }
});

updateStart();
