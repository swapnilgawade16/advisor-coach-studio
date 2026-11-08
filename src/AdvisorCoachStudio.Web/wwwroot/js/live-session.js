import { RealtimeSessionClient } from './realtime-client.js';
import { formatClock, showMessage } from './site.js';

const STATE_TEXT = {
  idle: 'Not started',
  connecting: 'Connecting\u2026',
  ready: 'Connected \u2014 the client will speak first',
  listening: 'Your turn to speak',
  client_speaking: 'Client speaking',
  interrupted: 'Interrupted',
  ending: 'Ending\u2026',
  ended: 'Session ended',
  failed: 'Could not start',
};

const ANNOUNCED_STATES = new Set(['ready', 'ending', 'ended', 'failed']);

const ENDED_TEXT = {
  user_ended: 'You ended the session.',
  duration_limit: 'The time limit was reached, so the session ended.',
  disconnected: 'The connection was lost, so the session ended. Completed transcript turns were kept.',
  provider_error: 'The voice service had a problem, so the session ended. Completed transcript turns were kept.',
  connection_quality: 'The session ended because the connection could not keep up. Completed transcript turns were kept.',
  access_expired: 'Your access expired, so the session ended.',
};

/**
 * Drives the shared live-conversation panel (Pages/Shared/_LiveConversation.cshtml) for one roleplay session.
 * The AudioContext must have been created/resumed inside the user's Start gesture.
 */
export async function runLiveSession({ sessionId, websocketPath, reportUrl, maxSeconds, audioContext, mediaStream, difficultyName, clientName }) {
  const live = document.getElementById('live');
  const el = (id) => document.getElementById(id);
  const stateEl = el('live-state');
  const activity = el('live-activity');
  const announcer = el('live-announcer');
  const networkEl = el('live-network');
  const muteButton = el('mute-button');
  const endButton = el('end-button');
  const errorEl = el('live-error');
  const endedPanel = el('ended-panel');
  const endedText = el('ended-text');
  const reportLink = el('report-link');
  const transcriptList = el('transcript-list');
  const transcriptEmpty = el('transcript-empty');
  const elapsedEl = el('live-elapsed');

  live.hidden = false;
  clientName ??= live.dataset.clientName;
  if (difficultyName) el('live-difficulty').textContent = difficultyName;
  if (maxSeconds) el('live-max').textContent = formatClock(maxSeconds);
  reportLink.href = reportUrl;
  live.dataset.sessionId = sessionId;

  let currentState = 'connecting';
  let muted = false;
  let readyAt = 0;
  let timer = 0;
  let underruns = 0;

  const announce = (text) => { announcer.textContent = text; };

  // Name the client where it helps ("Alex Morgan is speaking"); other states use the shared wording.
  const stateLabel = (state) => {
    if (clientName && state === 'client_speaking') return `${clientName} is speaking`;
    if (clientName && state === 'ready') return `Connected \u2014 ${clientName} will speak first`;
    return STATE_TEXT[state] ?? state;
  };

  const renderState = () => {
    const base = stateLabel(currentState);
    stateEl.textContent = muted && !['ending', 'ended', 'failed'].includes(currentState) ? `${base} (microphone muted)` : base;
    stateEl.dataset.state = currentState;
    activity.classList.toggle('speaking', currentState === 'client_speaking');
    activity.classList.toggle('listening', currentState === 'listening' && !muted);
  };

  const setState = (state) => {
    if (state === currentState) return;
    currentState = state;
    renderState();
    if (ANNOUNCED_STATES.has(state)) announce(stateLabel(state));
  };

  const upsertTurn = ({ turnId, speaker, text, final, status, fullyHeard }) => {
    let item = transcriptList.querySelector(`[data-turn-id="${CSS.escape(String(turnId))}"]`);
    if (final && status === 'final' && !text.trim()) {
      item?.remove();
      transcriptEmpty.hidden = transcriptList.children.length > 0;
      return;
    }
    if (!item) {
      item = document.createElement('li');
      item.dataset.turnId = String(turnId);
      const who = document.createElement('span');
      who.className = 'speaker';
      const note = document.createElement('span');
      note.className = 'tag tag-warn';
      note.hidden = true;
      const body = document.createElement('p');
      item.append(who, note, body);
      transcriptList.append(item);
    }
    item.className = `speaker-${speaker}${final ? '' : ' provisional'}`;
    item.children[0].textContent = speaker === 'advisor' ? 'You' : (clientName ?? 'Client');
    const note = item.children[1];
    const noteText = status === 'interrupted' || fullyHeard === false ? 'Interrupted: not fully heard'
      : status === 'failed' ? 'Captions unavailable'
      : final ? '' : 'Provisional';
    note.textContent = noteText;
    note.hidden = !noteText;
    item.children[2].textContent = text || (status === 'failed' ? '(no caption available)' : '');
    transcriptEmpty.hidden = true;
  };

  const finish = (detail, failure) => {
    window.clearInterval(timer);
    muteButton.disabled = true;
    endButton.disabled = true;
    activity.classList.remove('speaking', 'listening');
    endedText.textContent = failure
      ? 'The session could not start. You can start a new attempt from the scenario page.'
      : `${ENDED_TEXT[detail?.reason] ?? 'The session ended.'}${detail?.transcriptStatus === 'incomplete' ? ' Some captions were incomplete.' : ''}`;
    if (failure) {
      reportLink.textContent = 'Back to scenarios';
      reportLink.href = '/';
    } else if (detail?.reportUrl) {
      reportLink.href = detail.reportUrl;
    }
    endedPanel.hidden = false;
    reportLink.focus();
  };

  const client = new RealtimeSessionClient({ url: websocketPath, mode: 'roleplay', audioContext, mediaStream });
  client.addEventListener('state', (event) => setState(event.detail.state));
  client.addEventListener('transcript', (event) => upsertTurn(event.detail));
  client.addEventListener('error', (event) => showMessage(errorEl, event.detail.message));
  client.addEventListener('network', (event) => {
    showMessage(networkEl, event.detail.congested ? 'Your connection is struggling; audio may be delayed.' : '');
  });
  client.addEventListener('underrun', () => {
    underruns += 1;
    if (underruns >= 8) showMessage(networkEl, 'Audio playback is stuttering. Check your network connection.');
  });
  client.addEventListener('overflow', () => showMessage(networkEl, 'Audio arrived faster than it could be played.'));
  client.addEventListener('ended', (event) => {
    setState('ended');
    finish(event.detail, false);
  });

  muteButton.addEventListener('click', () => {
    muted = !muted;
    client.setMuted(muted);
    muteButton.setAttribute('aria-pressed', String(muted));
    muteButton.textContent = muted ? 'Unmute microphone' : 'Mute microphone';
    renderState();
    announce(muted ? 'Microphone muted' : 'Microphone on');
  });

  endButton.addEventListener('click', () => {
    endButton.disabled = true;
    muteButton.disabled = true;
    client.end('user');
  });

  renderState();
  try {
    await client.start();
  } catch (error) {
    setState('failed');
    showMessage(errorEl, error?.message ?? 'The practice session could not start.');
    finish(null, true);
    return client;
  }

  readyAt = Date.now();
  muteButton.disabled = false;
  endButton.disabled = false;
  if (audioContext.state !== 'running') {
    // Autoplay policy: some browsers keep the context suspended; resume on the next user interaction.
    showMessage(networkEl, 'Your browser paused audio playback. Click or press a key anywhere on the page to hear the client.');
    const resume = () => {
      audioContext.resume().then(() => {
        if (audioContext.state === 'running') showMessage(networkEl, '');
      }).catch(() => {});
    };
    document.addEventListener('pointerdown', resume, { once: true });
    document.addEventListener('keydown', resume, { once: true });
  }
  timer = window.setInterval(() => {
    const elapsed = Math.min(maxSeconds || Infinity, (Date.now() - readyAt) / 1000);
    elapsedEl.textContent = formatClock(elapsed);
    // Drives the decorative time bar; set through the CSSOM, which the style-src 'self' policy allows.
    if (maxSeconds) live.style.setProperty('--progress', String(Math.min(1, elapsed / maxSeconds)));
  }, 500);
  return client;
}
