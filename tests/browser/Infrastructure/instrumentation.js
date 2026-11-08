// Test-only instrumentation, injected with page.AddInitScriptAsync before any page script runs.
// It observes the product without changing its behaviour: getUserMedia calls and the streams they return,
// WebSocket messages sent, AudioWorklet port messages, live-state transitions, and live transcript updates.
(() => {
  if (window.__acs) return;

  const acs = {
    getUserMediaCalls: 0,
    getUserMediaErrors: [],
    streams: [],
    sockets: [],
    sent: [],
    audioAppends: 0,
    received: [],
    audioDeltas: 0,
    closes: [],
    worklets: [],
    states: [],
    transcriptAdded: [],
    transcriptUpdates: [],
  };
  window.__acs = acs;

  acs.trackStates = () => acs.streams.flatMap((stream) => stream.getTracks().map((track) => track.readyState));
  acs.snapshot = () => ({
    getUserMediaCalls: acs.getUserMediaCalls,
    getUserMediaErrors: acs.getUserMediaErrors.slice(),
    trackStates: acs.trackStates(),
    socketStates: acs.sockets.map((socket) => socket.readyState),
    socketUrls: acs.sockets.map((socket) => socket.url),
    sent: acs.sent.slice(),
    audioAppends: acs.audioAppends,
    received: acs.received.slice(),
    audioDeltas: acs.audioDeltas,
    closes: acs.closes.slice(),
    worklets: acs.worklets.map((worklet) => ({ name: worklet.name, messages: worklet.messages.slice() })),
    states: acs.states.slice(),
    transcriptAdded: acs.transcriptAdded.slice(),
    transcriptUpdates: acs.transcriptUpdates.slice(),
  });

  // Microphone: count calls and keep every returned stream so tests can check track.readyState.
  const mediaDevices = navigator.mediaDevices;
  if (mediaDevices && typeof mediaDevices.getUserMedia === 'function') {
    const getUserMedia = mediaDevices.getUserMedia.bind(mediaDevices);
    mediaDevices.getUserMedia = async (constraints) => {
      acs.getUserMediaCalls += 1;
      try {
        const stream = await getUserMedia(constraints);
        acs.streams.push(stream);
        return stream;
      } catch (error) {
        acs.getUserMediaErrors.push(error && error.name ? error.name : String(error));
        throw error;
      }
    };
  }

  // WebSockets: remember every socket and the type of every message sent (audio.append is only counted).
  const NativeWebSocket = window.WebSocket;
  const nativeSend = NativeWebSocket.prototype.send;
  NativeWebSocket.prototype.send = function send(data) {
    if (typeof data === 'string') {
      const match = /"type":"([^"]+)"/.exec(data.slice(0, 80));
      const type = match ? match[1] : 'unknown';
      if (type === 'audio.append') acs.audioAppends += 1;
      else acs.sent.push(type);
    }
    return nativeSend.call(this, data);
  };
  class TrackedWebSocket extends NativeWebSocket {
    constructor(...args) {
      super(...args);
      acs.sockets.push(this);
      this.addEventListener('message', (event) => {
        if (typeof event.data !== 'string') return;
        const match = /"type":"([^"]+)"/.exec(event.data.slice(0, 80));
        const type = match ? match[1] : 'unknown';
        if (type === 'audio.delta') acs.audioDeltas += 1;
        else acs.received.push(type === 'session.state' ? `state:${JSON.parse(event.data).state}` : type);
      });
      this.addEventListener('close', (event) => acs.closes.push({ code: event.code, reason: event.reason, wasClean: event.wasClean }));
    }
  }
  window.WebSocket = TrackedWebSocket;

  // AudioWorklet nodes: record control messages (mute, clear, stop, ...) posted to capture/playback processors.
  const NativeWorkletNode = window.AudioWorkletNode;
  if (NativeWorkletNode) {
    class TrackedAudioWorkletNode extends NativeWorkletNode {
      constructor(context, name, options) {
        super(context, name, options);
        const entry = { name, messages: [] };
        acs.worklets.push(entry);
        const port = this.port;
        const post = port.postMessage.bind(port);
        port.postMessage = (message, transfer) => {
          const type = message && typeof message === 'object' ? message.type : typeof message;
          if (type !== 'enqueue') entry.messages.push(message && message.type === 'mute' ? `mute:${message.muted}` : type);
          return transfer === undefined ? post(message) : post(message, transfer);
        };
      }
    }
    window.AudioWorkletNode = TrackedAudioWorkletNode;
  }

  // Live panel: state transitions (data-state on #live-state) and transcript list items as they change.
  const observe = (records) => {
    for (const record of records) {
      const target = record.target;
      if (record.type === 'attributes' && target.id === 'live-state' && record.attributeName === 'data-state') {
        const state = target.getAttribute('data-state');
        if (acs.states[acs.states.length - 1] !== state) acs.states.push(state);
      } else if (record.type === 'childList' && target.id === 'transcript-list') {
        for (const node of record.addedNodes) {
          if (node.nodeType === 1) acs.transcriptAdded.push(node.dataset.turnId ?? null);
        }
      } else if (record.type === 'attributes' && record.attributeName === 'class' && target.parentElement?.id === 'transcript-list') {
        acs.transcriptUpdates.push({
          turnId: target.dataset.turnId ?? null,
          className: target.className,
          text: target.lastElementChild ? target.lastElementChild.textContent : '',
        });
      }
    }
  };
  new MutationObserver(observe).observe(document, { subtree: true, childList: true, attributes: true, attributeFilter: ['data-state', 'class'] });
  document.addEventListener('DOMContentLoaded', () => {
    const state = document.getElementById('live-state')?.getAttribute('data-state');
    if (state && acs.states.length === 0) acs.states.push(state);
  }, { once: true });

  // Unload: after the product's own pagehide/beforeunload cleanup has run, persist what was still live so a test
  // can check it after a reload. Re-registering keeps this listener last in the pagehide listener list.
  const persistUnload = () => {
    try {
      sessionStorage.setItem('__acs_unload', JSON.stringify({
        trackStates: acs.trackStates(),
        socketStates: acs.sockets.map((socket) => socket.readyState),
        sent: acs.sent.slice(-50),
      }));
    } catch {
      // Storage unavailable: the test will report the missing record.
    }
  };
  const nativeAddEventListener = EventTarget.prototype.addEventListener;
  nativeAddEventListener.call(window, 'pagehide', persistUnload);
  EventTarget.prototype.addEventListener = function addEventListener(type, listener, options) {
    const result = nativeAddEventListener.call(this, type, listener, options);
    if (this === window && type === 'pagehide' && listener !== persistUnload) {
      window.removeEventListener('pagehide', persistUnload);
      nativeAddEventListener.call(window, 'pagehide', persistUnload);
    }
    return result;
  };
})();
