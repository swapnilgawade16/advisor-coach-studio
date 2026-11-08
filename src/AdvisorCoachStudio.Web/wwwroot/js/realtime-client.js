// Browser side of the acs.v1 application protocol: microphone capture, PCM playback, and the
// session WebSocket. The browser never talks to Azure; see docs/application-protocol.md.
import { FRAME_SAMPLES, WIRE_SAMPLE_RATE, base64ToBytes, bytesToBase64 } from './audio-format.js';

export const PROTOCOL = 'acs.v1';

/** Recommended getUserMedia constraints for roleplay capture (headphones are still recommended). */
export const MICROPHONE_CONSTRAINTS = Object.freeze({
  audio: Object.freeze({ channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true }),
  video: false,
});

const PROTOCOL_VERSION = 1;
const HEARTBEAT_INTERVAL_MS = 10_000;
const READY_TIMEOUT_MS = 15_000;
const END_TIMEOUT_MS = 8_000;
const CLEAR_ACK_TIMEOUT_MS = 250;
const CONGESTION_HIGH_WATER_BYTES = 96 * 1024;
const CONGESTION_LOW_WATER_BYTES = 32 * 1024;
const CONGESTION_FATAL_MS = 3_000;
const CONGESTION_POLL_MS = 250;
const PROGRESS_MIN_INTERVAL_MS = 150;
const MAX_PLAYBACK_BUFFER_MS = 3_000;
const MAX_REMEMBERED_RESPONSES = 32;
const MAX_TRACKED_PROGRESS_ITEMS = 64;
const CAPTURE_PROCESSOR = 'acs-capture';
const PLAYBACK_PROCESSOR = 'acs-playback';
const DEFAULT_WORKLET_BASE_URL = new URL('./', import.meta.url).href;

/** Error used for start() rejections; mirrors the fields of the 'error' event. */
export class RealtimeClientError extends Error {
  constructor(code, message, { retryable = false, correlationId = null, detail = null, cause } = {}) {
    super(message, cause === undefined ? undefined : { cause });
    this.name = 'RealtimeClientError';
    this.code = code;
    this.retryable = retryable;
    this.correlationId = correlationId;
    this.detail = detail;
  }
}

const workletModules = new WeakMap();

// addModule once per AudioContext and URL; a failed load is retried on the next call.
function loadWorkletModule(audioContext, url) {
  let modules = workletModules.get(audioContext);
  if (!modules) {
    modules = new Map();
    workletModules.set(audioContext, modules);
  }
  let promise = modules.get(url);
  if (!promise) {
    promise = audioContext.audioWorklet.addModule(url).catch((error) => {
      modules.delete(url);
      throw error;
    });
    modules.set(url, promise);
  }
  return promise;
}

function toWebSocketUrl(url) {
  const resolved = new URL(String(url), globalThis.location ? globalThis.location.href : undefined);
  if (resolved.protocol === 'http:') resolved.protocol = 'ws:';
  else if (resolved.protocol === 'https:') resolved.protocol = 'wss:';
  return resolved.href;
}

function itemKey(responseId, itemId, contentIndex) {
  return `${responseId}\u0000${itemId}\u0000${contentIndex}`;
}

function rememberBounded(set, value, max) {
  set.delete(value);
  set.add(value);
  while (set.size > max) set.delete(set.values().next().value);
}

function safeDisconnect(node) {
  if (!node) return;
  try {
    node.disconnect();
  } catch {
    // Already disconnected or the context is closed.
  }
}

function wait(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * One live roleplay or demonstration attempt. Single use: create a new instance per session.
 * Events (CustomEvent detail): state, ready, transcript, level, playback, underrun, overflow,
 * network, error, ended.
 */
export class RealtimeSessionClient extends EventTarget {
  constructor({ url, mode = 'roleplay', audioContext, mediaStream = null, workletBaseUrl = DEFAULT_WORKLET_BASE_URL } = {}) {
    super();
    if (!url) throw new TypeError('A WebSocket url is required.');
    if (mode !== 'roleplay' && mode !== 'demonstration') throw new TypeError("mode must be 'roleplay' or 'demonstration'.");
    if (!audioContext || typeof audioContext.createGain !== 'function') throw new TypeError('An AudioContext is required.');
    if (mode === 'roleplay' && (!mediaStream || typeof mediaStream.getAudioTracks !== 'function')) {
      throw new TypeError('Roleplay mode requires a microphone MediaStream.');
    }

    this._url = url;
    this._mode = mode;
    this._audioContext = audioContext;
    this._mediaStream = mode === 'roleplay' ? mediaStream : null;
    this._workletBaseUrl = String(workletBaseUrl).replace(/\/?$/, '/');

    this._state = 'idle';
    this._ws = null;
    this._opened = false;
    this._ready = false;
    this._ending = false;
    this._endSent = false;
    this._terminated = false;
    this._disposed = false;
    this._muted = false;
    this._playing = false;
    this._sessionInfo = null;
    this._endedDetail = null;

    this._sourceNode = null;
    this._captureNode = null;
    this._captureSink = null;
    this._playbackNode = null;

    this._clientSequence = 0;
    this._framesSent = 0;
    this._framesNotSent = 0;
    this._congested = false;
    this._congestedSince = 0;
    this._congestionTimer = 0;
    this._heartbeatTimer = 0;
    this._readyTimer = 0;
    this._underruns = 0;
    this._overflows = 0;

    this._lastAudioSeq = -Infinity;
    this._currentAudio = null;
    this._clearedResponses = new Set();
    this._progress = new Map();
    this._clearRequests = new Map();
    this._clearRequestSeq = 0;

    this._startPromise = null;
    this._startSettle = null;
    this._endPromise = null;
    this._finished = new Promise((resolve) => {
      this._resolveFinished = resolve;
    });
    this._onPageHide = () => this.dispose();
  }

  get state() { return this._state; }
  get mode() { return this._mode; }
  get ready() { return this._ready; }
  get muted() { return this._muted; }
  get playing() { return this._playing; }
  get framesSent() { return this._framesSent; }
  get framesNotSent() { return this._framesNotSent; }
  get underruns() { return this._underruns; }
  get overflows() { return this._overflows; }
  get sessionInfo() { return this._sessionInfo; }

  /** Resolves with the session.ready payload; rejects with RealtimeClientError if the session cannot become ready. */
  start() {
    if (this._startPromise) return this._startPromise;
    if (this._terminated || this._disposed) {
      return Promise.reject(new RealtimeClientError('client_closed', 'This session client has already been closed.'));
    }
    this._startPromise = new Promise((resolve, reject) => {
      this._startSettle = { resolve, reject };
    });
    // Failures are also reported through the 'error' event; avoid unhandled-rejection noise.
    this._startPromise.catch(() => {});
    this._setup().catch((error) => this._failStart(error));
    return this._startPromise;
  }

  async _setup() {
    const context = this._audioContext;
    if (typeof AudioWorkletNode === 'undefined' || !context.audioWorklet || typeof WebSocket === 'undefined') {
      throw new RealtimeClientError('unsupported_browser', 'This browser does not support the audio features required for live practice.');
    }
    if (this._mediaStream && !this._mediaStream.getAudioTracks().some((track) => track.readyState === 'live')) {
      throw new RealtimeClientError('microphone_unavailable', 'No active microphone track is available.');
    }

    globalThis.addEventListener?.('pagehide', this._onPageHide);
    globalThis.addEventListener?.('beforeunload', this._onPageHide);
    this._readyTimer = setTimeout(
      () => this._failStart(new RealtimeClientError('connection_timeout', 'The practice session did not become ready in time.', { retryable: true })),
      READY_TIMEOUT_MS,
    );
    this._setState('connecting', true);
    // Playback must already be unlocked by the user's Start gesture; never block on resume().
    if (context.state === 'suspended') context.resume().catch(() => {});

    const modules = [loadWorkletModule(context, new URL('audio-playback-worklet.js', this._workletBaseUrl).href)];
    if (this._mode === 'roleplay') {
      modules.push(loadWorkletModule(context, new URL('audio-capture-worklet.js', this._workletBaseUrl).href));
    }
    try {
      await Promise.all(modules);
    } catch (error) {
      throw new RealtimeClientError('audio_setup_failed', 'Live audio processing could not be initialised.', { cause: error });
    }
    if (this._terminated) return;

    this._createPlaybackNode();
    if (this._mode === 'roleplay') this._createCaptureGraph();
    this._openSocket();
  }

  _createPlaybackNode() {
    const context = this._audioContext;
    const node = new AudioWorkletNode(context, PLAYBACK_PROCESSOR, {
      numberOfInputs: 0,
      numberOfOutputs: 1,
      outputChannelCount: [1],
      processorOptions: { inputSampleRate: WIRE_SAMPLE_RATE, maxBufferedMs: MAX_PLAYBACK_BUFFER_MS },
    });
    node.port.onmessage = (event) => this._handlePlaybackMessage(event.data);
    node.connect(context.destination);
    this._playbackNode = node;
  }

  _createCaptureGraph() {
    const context = this._audioContext;
    const source = context.createMediaStreamSource(this._mediaStream);
    const capture = new AudioWorkletNode(context, CAPTURE_PROCESSOR, {
      numberOfInputs: 1,
      numberOfOutputs: 1,
      outputChannelCount: [1],
      processorOptions: { targetSampleRate: WIRE_SAMPLE_RATE, frameSamples: FRAME_SAMPLES },
    });
    // A zero-gain sink keeps the capture node pulled by the render graph without making it audible.
    const sink = context.createGain();
    sink.gain.value = 0;
    capture.port.onmessage = (event) => this._handleCaptureMessage(event.data);
    if (this._muted) capture.port.postMessage({ type: 'mute', muted: true });
    source.connect(capture);
    capture.connect(sink);
    sink.connect(context.destination);
    this._sourceNode = source;
    this._captureNode = capture;
    this._captureSink = sink;
  }

  _openSocket() {
    const ws = new WebSocket(toWebSocketUrl(this._url), PROTOCOL);
    this._ws = ws;
    ws.onopen = () => this._handleOpen();
    ws.onmessage = (event) => this._handleSocketMessage(event.data);
    ws.onerror = () => {}; // A close event always follows; it carries the outcome.
    ws.onclose = (event) => this._handleClose(event);
  }

  _handleOpen() {
    if (this._terminated) return;
    if (this._ws.protocol !== PROTOCOL) {
      this._failStart(new RealtimeClientError('protocol_mismatch', 'The server did not negotiate the acs.v1 protocol.'));
      return;
    }
    this._opened = true;
    this._heartbeatTimer = setInterval(() => this._send({ v: PROTOCOL_VERSION, type: 'heartbeat' }), HEARTBEAT_INTERVAL_MS);
  }

  _handleClose(event) {
    if (this._terminated) return;
    if (!this._ready) {
      const code = this._opened ? 'connection_closed' : 'connection_failed';
      this._failStart(new RealtimeClientError(code, 'The connection to the practice service was lost.', { retryable: true }));
      return;
    }
    this._terminate({
      reason: 'disconnected',
      transcriptStatus: null,
      reportStatus: null,
      reportUrl: null,
      unexpected: true,
      closeCode: event.code,
    });
  }

  _handleSocketMessage(data) {
    if (this._terminated || typeof data !== 'string') return;
    let message;
    try {
      message = JSON.parse(data);
    } catch {
      return;
    }
    if (!message || typeof message !== 'object' || message.v !== PROTOCOL_VERSION || typeof message.type !== 'string') return;
    switch (message.type) {
      case 'session.state':
        this._handleServerState(message);
        break;
      case 'session.ready':
        this._handleReady(message);
        break;
      case 'audio.delta':
        this._handleAudioDelta(message);
        break;
      case 'playback.clear':
        this._handlePlaybackClear(message);
        break;
      case 'transcript.partial':
        this._handleTranscript(message, false);
        break;
      case 'transcript.final':
        this._handleTranscript(message, true);
        break;
      case 'session.ended':
        this._handleSessionEnded(message);
        break;
      case 'error':
        this._handleServerError(message);
        break;
      default:
        break;
    }
  }

  _handleServerState(message) {
    const { state } = message;
    if (typeof state !== 'string') return;
    // Any state other than client_speaking means no more audio will arrive for the current item.
    if (state !== 'client_speaking') this._completeCurrentAudio();
    if (this._ending && state !== 'ending' && state !== 'ended') return;
    this._setState(state, false);
  }

  _handleReady(message) {
    if (this._ready) return;
    const audio = message.audio && typeof message.audio === 'object' ? message.audio : null;
    if (
      audio &&
      ((audio.sampleRate !== undefined && audio.sampleRate !== WIRE_SAMPLE_RATE) || (audio.channels !== undefined && audio.channels !== 1))
    ) {
      this._failStart(new RealtimeClientError('unsupported_audio_format', 'The server requested an unsupported audio format.'));
      return;
    }
    this._ready = true;
    clearTimeout(this._readyTimer);
    this._readyTimer = 0;

    const info = { ...message };
    delete info.v;
    delete info.type;
    this._sessionInfo = info;
    if (this._muted && this._mode === 'roleplay') this._send({ v: PROTOCOL_VERSION, type: 'microphone.muted', muted: true });
    if (this._state === 'connecting') this._setState('ready', true);
    this._emit('ready', info);

    const settle = this._startSettle;
    this._startSettle = null;
    if (settle) settle.resolve(info);
  }

  _handleAudioDelta(message) {
    const node = this._playbackNode;
    if (!node || this._ending) return;
    const { responseId, itemId, audio } = message;
    if (typeof responseId !== 'string' || typeof itemId !== 'string' || typeof audio !== 'string') return;
    if (typeof message.seq === 'number') {
      if (message.seq <= this._lastAudioSeq) return;
      this._lastAudioSeq = message.seq;
    }
    if (this._clearedResponses.has(responseId)) return;
    const contentIndex = Number.isInteger(message.contentIndex) ? message.contentIndex : 0;

    let bytes;
    try {
      bytes = base64ToBytes(audio);
    } catch {
      return;
    }
    if (bytes.byteLength === 0 || bytes.byteLength % 2 !== 0) return;

    const current = this._currentAudio;
    if (current && (current.responseId !== responseId || current.itemId !== itemId || current.contentIndex !== contentIndex)) {
      this._completeCurrentAudio();
    }
    this._currentAudio = { responseId, itemId, contentIndex };
    node.port.postMessage({ type: 'enqueue', responseId, itemId, contentIndex, pcm: bytes.buffer }, [bytes.buffer]);
  }

  _completeCurrentAudio() {
    const current = this._currentAudio;
    if (!current) return;
    this._currentAudio = null;
    if (this._playbackNode) this._playbackNode.port.postMessage({ type: 'complete', ...current });
  }

  _handlePlaybackClear(message) {
    const { responseId } = message;
    if (typeof responseId !== 'string') return;
    rememberBounded(this._clearedResponses, responseId, MAX_REMEMBERED_RESPONSES);
    if (this._currentAudio && this._currentAudio.responseId === responseId) this._currentAudio = null;
    for (const entry of this._progress.values()) {
      if (entry.responseId === responseId) this._cancelProgressTimer(entry);
    }
    // The worklet replies with 'cleared'; playback.stopped is sent per item that had started playing.
    this._requestClear(responseId, 'interrupt');
  }

  _handleTranscript(message, final) {
    if (message.turnId === undefined || message.turnId === null) return;
    this._emit('transcript', {
      turnId: message.turnId,
      speaker: typeof message.speaker === 'string' ? message.speaker : null,
      text: typeof message.text === 'string' ? message.text : '',
      final,
      status: final ? (typeof message.status === 'string' ? message.status : 'final') : 'partial',
      fullyHeard: final && typeof message.fullyHeard === 'boolean' ? message.fullyHeard : null,
      sequence: final && typeof message.sequence === 'number' ? message.sequence : null,
    });
  }

  _handleSessionEnded(message) {
    const detail = {
      reason: typeof message.reason === 'string' ? message.reason : 'unknown',
      transcriptStatus: typeof message.transcriptStatus === 'string' ? message.transcriptStatus : null,
      reportStatus: typeof message.reportStatus === 'string' ? message.reportStatus : null,
      reportUrl: typeof message.reportUrl === 'string' ? message.reportUrl : null,
      unexpected: false,
    };
    if (!this._ready) {
      const retryable = detail.reason !== 'access_expired' && detail.reason !== 'user_ended';
      this._failStart(new RealtimeClientError(detail.reason, 'The session ended before it was ready.', { retryable, detail }));
      return;
    }
    this._terminate(detail);
  }

  _handleServerError(message) {
    const error = {
      code: typeof message.code === 'string' ? message.code : 'server_error',
      message: typeof message.message === 'string' ? message.message : 'The practice service reported an error.',
      retryable: message.retryable === true,
      correlationId: typeof message.correlationId === 'string' ? message.correlationId : null,
    };
    if (!this._ready) {
      this._failStart(new RealtimeClientError(error.code, error.message, error));
      return;
    }
    this._emit('error', error);
  }

  _handleCaptureMessage(message) {
    if (!message || typeof message !== 'object') return;
    if (message.type === 'frame') this._sendAudioFrame(message.pcm);
    else if (message.type === 'level') this._emit('level', { rms: Number(message.rms) || 0 });
  }

  _sendAudioFrame(pcm) {
    if (!this._ready || this._muted || this._ending || this._terminated || !(pcm instanceof ArrayBuffer)) return;
    const ws = this._ws;
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    // Never queue audio without bound: skip (and count) frames while the socket is backed up.
    if (this._congested) {
      if (ws.bufferedAmount > CONGESTION_LOW_WATER_BYTES) {
        this._framesNotSent += 1;
        this._checkCongestion();
        return;
      }
      this._setCongested(false);
    } else if (ws.bufferedAmount > CONGESTION_HIGH_WATER_BYTES) {
      this._framesNotSent += 1;
      this._setCongested(true);
      return;
    }
    const clientSequence = this._clientSequence + 1;
    const audio = bytesToBase64(new Uint8Array(pcm));
    if (this._send({ v: PROTOCOL_VERSION, type: 'audio.append', clientSequence, audio })) {
      this._clientSequence = clientSequence;
      this._framesSent += 1;
    } else {
      this._framesNotSent += 1;
    }
  }

  _setCongested(congested) {
    if (this._congested === congested) return;
    this._congested = congested;
    clearInterval(this._congestionTimer);
    this._congestionTimer = 0;
    if (congested) {
      this._congestedSince = performance.now();
      this._congestionTimer = setInterval(() => this._checkCongestion(), CONGESTION_POLL_MS);
    }
    this._emit('network', {
      congested,
      bufferedAmount: this._ws ? this._ws.bufferedAmount : 0,
      framesNotSent: this._framesNotSent,
    });
  }

  _checkCongestion() {
    const ws = this._ws;
    if (!this._congested || this._terminated || !ws) return;
    if (ws.bufferedAmount <= CONGESTION_LOW_WATER_BYTES) {
      this._setCongested(false);
    } else if (!this._ending && performance.now() - this._congestedSince > CONGESTION_FATAL_MS) {
      clearInterval(this._congestionTimer);
      this._congestionTimer = 0;
      this._emit('error', {
        code: 'connection_quality',
        message: 'The network connection is too slow to stream audio reliably.',
        retryable: true,
        correlationId: null,
      });
      void this.end('connection_quality');
    }
  }

  _handlePlaybackMessage(message) {
    if (!message || typeof message !== 'object') return;
    switch (message.type) {
      case 'progress':
        this._handlePlaybackProgress(message.items);
        break;
      case 'cleared':
        this._handlePlaybackCleared(message);
        break;
      case 'playing': {
        const playing = message.playing === true;
        if (playing !== this._playing) {
          this._playing = playing;
          this._emit('playback', { playing });
        }
        break;
      }
      case 'underrun':
        this._underruns = Number(message.count) || this._underruns + 1;
        this._emit('underrun', { count: this._underruns });
        break;
      case 'overflow':
        this._overflows += 1;
        this._emit('overflow', {
          responseId: message.responseId,
          itemId: message.itemId,
          droppedMs: message.droppedMs,
          count: this._overflows,
        });
        break;
      default:
        break;
    }
  }

  _handlePlaybackProgress(items) {
    if (!Array.isArray(items) || this._terminated) return;
    const now = performance.now();
    for (const item of items) {
      if (!item || typeof item.responseId !== 'string' || typeof item.itemId !== 'string') continue;
      if (this._clearedResponses.has(item.responseId)) continue; // playback.stopped carries the final position
      const entry = this._progressEntry(item);
      const playedMs = Math.max(0, Math.floor(item.playedMs) || 0);
      if (entry.stopped || playedMs <= entry.sentMs) continue;
      const elapsed = now - entry.sentAt;
      if (item.done === true || elapsed >= PROGRESS_MIN_INTERVAL_MS) {
        this._sendProgress(entry, playedMs);
      } else {
        // Throttled: send the latest position when the interval elapses.
        entry.pendingMs = playedMs;
        if (!entry.timer) {
          entry.timer = setTimeout(() => {
            entry.timer = 0;
            this._sendProgress(entry, entry.pendingMs);
          }, PROGRESS_MIN_INTERVAL_MS - elapsed);
        }
      }
    }
  }

  _handlePlaybackCleared(message) {
    const request = this._clearRequests.get(message.requestId);
    if (request) this._clearRequests.delete(message.requestId);
    const interrupted = !request || request.purpose === 'interrupt';
    const items = Array.isArray(message.items) ? message.items : [];
    for (const item of items) {
      if (!item || typeof item.responseId !== 'string' || typeof item.itemId !== 'string') continue;
      const entry = this._progressEntry(item);
      if (entry.stopped) continue;
      const playedMs = Math.max(0, entry.sentMs, Math.floor(item.playedMs) || 0);
      if (interrupted) {
        this._cancelProgressTimer(entry);
        this._send({
          v: PROTOCOL_VERSION,
          type: 'playback.stopped',
          responseId: entry.responseId,
          itemId: entry.itemId,
          contentIndex: entry.contentIndex,
          playedMilliseconds: playedMs,
        });
        entry.sentMs = playedMs;
      } else {
        this._sendProgress(entry, playedMs); // final position before session.end
      }
      entry.stopped = true;
    }
    if (request) request.resolve(items);
  }

  _progressEntry({ responseId, itemId, contentIndex }) {
    const index = Number.isInteger(contentIndex) ? contentIndex : 0;
    const key = itemKey(responseId, itemId, index);
    let entry = this._progress.get(key);
    if (!entry) {
      entry = { responseId, itemId, contentIndex: index, sentMs: -1, sentAt: -Infinity, pendingMs: -1, timer: 0, stopped: false };
      this._progress.set(key, entry);
      if (this._progress.size > MAX_TRACKED_PROGRESS_ITEMS) {
        for (const [oldKey, old] of this._progress) {
          if (oldKey !== key && !old.timer) {
            this._progress.delete(oldKey);
            break;
          }
        }
      }
    }
    return entry;
  }

  _sendProgress(entry, playedMs) {
    this._cancelProgressTimer(entry);
    if (entry.stopped || this._endSent || !(playedMs > entry.sentMs)) return;
    const sent = this._send({
      v: PROTOCOL_VERSION,
      type: 'playback.progress',
      responseId: entry.responseId,
      itemId: entry.itemId,
      contentIndex: entry.contentIndex,
      playedMilliseconds: playedMs,
    });
    if (sent) {
      entry.sentMs = playedMs;
      entry.sentAt = performance.now();
    }
  }

  _cancelProgressTimer(entry) {
    if (entry.timer) {
      clearTimeout(entry.timer);
      entry.timer = 0;
    }
    entry.pendingMs = -1;
  }

  _requestClear(responseId, purpose) {
    const node = this._playbackNode;
    if (!node) return Promise.resolve([]);
    const requestId = ++this._clearRequestSeq;
    return new Promise((resolve) => {
      this._clearRequests.set(requestId, { purpose, resolve });
      const message = { type: 'clear', requestId };
      if (responseId !== null) message.responseId = responseId;
      node.port.postMessage(message);
    });
  }

  /** Roleplay only. Muting stops samples at the capture worklet; nothing is sent while muted. */
  setMuted(muted) {
    const value = Boolean(muted);
    if (this._mode !== 'roleplay' || this._terminated || value === this._muted) return;
    this._muted = value;
    if (this._captureNode) this._captureNode.port.postMessage({ type: 'mute', muted: value });
    if (this._ready && !this._ending) this._send({ v: PROTOCOL_VERSION, type: 'microphone.muted', muted: value });
  }

  /**
   * Idempotent. Stops capture and playback immediately, sends session.end, then waits up to 8 s for
   * session.ended. Resolves with the 'ended' detail (null if the session never became ready).
   */
  end(reason = 'user') {
    if (!this._endPromise) this._endPromise = this._end(typeof reason === 'string' && reason ? reason : 'user');
    return this._endPromise;
  }

  async _end(reason) {
    if (this._terminated) return this._endedDetail;
    this._ending = true;
    this._setState('ending', true);
    this._stopCapture();
    clearInterval(this._congestionTimer);
    this._congestionTimer = 0;
    this._currentAudio = null;
    const cleared = this._requestClear(null, 'end'); // output is silent from the next render quantum

    if (!this._ready) {
      this._send({ v: PROTOCOL_VERSION, type: 'session.end', reason });
      this._endSent = true;
      this._failStart(new RealtimeClientError('aborted', 'The session was ended before it was ready.'), false);
      return null;
    }

    // Report final played positions before session.end so the server can truncate accurately.
    await Promise.race([cleared, wait(CLEAR_ACK_TIMEOUT_MS)]);
    if (this._terminated) return this._endedDetail;
    this._send({ v: PROTOCOL_VERSION, type: 'session.end', reason });
    this._endSent = true;
    const timer = setTimeout(
      () => this._terminate({ reason: 'disconnected', transcriptStatus: null, reportStatus: null, reportUrl: null, unexpected: true }),
      END_TIMEOUT_MS,
    );
    await this._finished;
    clearTimeout(timer);
    return this._endedDetail;
  }

  /** Idempotent full cleanup without further events. Runs automatically on pagehide/beforeunload. */
  dispose() {
    if (this._disposed) return;
    if (!this._terminated && this._ready && !this._endSent) {
      this._send({ v: PROTOCOL_VERSION, type: 'session.end', reason: 'user' });
      this._endSent = true;
    }
    this._disposed = true;
    const settle = this._startSettle;
    this._startSettle = null;
    this._terminate(null);
    this._removePageListeners();
    if (settle) settle.reject(new RealtimeClientError('aborted', 'The session client was disposed.'));
  }

  _failStart(error, emitError = true) {
    if (this._terminated) return;
    const failure =
      error instanceof RealtimeClientError
        ? error
        : new RealtimeClientError('setup_failed', 'The live session could not be started.', { cause: error });
    const payload = { code: failure.code, message: failure.message, retryable: failure.retryable, correlationId: failure.correlationId };
    if (this._ready) {
      this._emit('error', payload);
      this._terminate({ reason: failure.code, transcriptStatus: null, reportStatus: null, reportUrl: null, unexpected: true });
      return;
    }
    const settle = this._startSettle;
    this._startSettle = null;
    this._terminate(null, emitError ? 'failed' : 'ended');
    if (emitError) this._emit('error', payload);
    if (settle) settle.reject(failure);
  }

  _terminate(detail, finalState = 'ended') {
    if (this._terminated) return;
    this._terminated = true;
    this._stopCapture();
    this._stopPlayback();
    clearInterval(this._heartbeatTimer);
    clearTimeout(this._readyTimer);
    clearInterval(this._congestionTimer);
    this._heartbeatTimer = 0;
    this._readyTimer = 0;
    this._congestionTimer = 0;
    for (const entry of this._progress.values()) this._cancelProgressTimer(entry);
    for (const request of this._clearRequests.values()) request.resolve([]);
    this._clearRequests.clear();
    this._closeSocket();
    this._removePageListeners();
    this._endedDetail = detail;
    this._setState(finalState, true);
    if (detail) this._emit('ended', detail);
    this._resolveFinished(detail);
  }

  _stopCapture() {
    const capture = this._captureNode;
    if (capture) {
      capture.port.onmessage = null;
      capture.port.postMessage({ type: 'stop' });
    }
    safeDisconnect(this._sourceNode);
    safeDisconnect(capture);
    safeDisconnect(this._captureSink);
    this._sourceNode = null;
    this._captureNode = null;
    this._captureSink = null;
    if (this._mediaStream) {
      for (const track of this._mediaStream.getTracks()) track.stop();
    }
  }

  _stopPlayback() {
    const node = this._playbackNode;
    if (!node) return;
    this._playbackNode = null;
    node.port.onmessage = null;
    node.port.postMessage({ type: 'stop' });
    safeDisconnect(node);
    if (this._playing) {
      this._playing = false;
      this._emit('playback', { playing: false });
    }
  }

  _closeSocket() {
    const ws = this._ws;
    if (!ws) return;
    this._ws = null;
    ws.onopen = null;
    ws.onmessage = null;
    ws.onerror = null;
    ws.onclose = null;
    if (ws.readyState === WebSocket.CONNECTING || ws.readyState === WebSocket.OPEN) {
      try {
        ws.close(1000);
      } catch {
        // Closing is best effort.
      }
    }
  }

  _removePageListeners() {
    globalThis.removeEventListener?.('pagehide', this._onPageHide);
    globalThis.removeEventListener?.('beforeunload', this._onPageHide);
  }

  _send(message) {
    const ws = this._ws;
    if (!ws || ws.readyState !== WebSocket.OPEN) return false;
    try {
      ws.send(JSON.stringify(message));
      return true;
    } catch {
      return false;
    }
  }

  _setState(state, local) {
    if (this._state === state) return;
    this._state = state;
    this._emit('state', { state, local });
  }

  _emit(type, detail) {
    if (!this._disposed) this.dispatchEvent(new CustomEvent(type, { detail }));
  }
}
