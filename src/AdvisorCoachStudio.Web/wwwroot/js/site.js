// Shared helpers for page scripts. No third-party dependencies.

export function csrfToken() {
  return document.querySelector('meta[name="csrf-token"]')?.content ?? '';
}

export class ApiError extends Error {
  constructor(message, { status, code, retryable, correlationId } = {}) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.retryable = !!retryable;
    this.correlationId = correlationId;
  }
}

/** Same-origin JSON request with the antiforgery header on state-changing methods. */
export async function apiFetch(url, { method = 'GET', body } = {}) {
  const headers = { Accept: 'application/json' };
  if (method !== 'GET') headers['X-CSRF-TOKEN'] = csrfToken();
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  let response;
  try {
    response = await fetch(url, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      credentials: 'same-origin',
      cache: 'no-store',
    });
  } catch {
    throw new ApiError('The network request failed. Check your connection and try again.', { retryable: true });
  }

  const text = await response.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = null; }
  }

  if (!response.ok) {
    if (response.status === 401) {
      window.location.assign('/access');
    }
    throw new ApiError(data?.title ?? `Request failed (${response.status}).`, {
      status: response.status,
      code: data?.code,
      retryable: data?.retryable,
      correlationId: data?.correlationId,
    });
  }

  return data;
}

/** True when the browser has every API the live practice needs. */
export function supportsLiveAudio() {
  return !!(navigator.mediaDevices?.getUserMedia && window.AudioContext && window.AudioWorkletNode && window.WebSocket);
}

export function formatClock(totalSeconds) {
  const seconds = Math.max(0, Math.floor(totalSeconds));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

export function prefersReducedMotion() {
  return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
}

export function showMessage(element, message) {
  if (!element) return;
  element.textContent = message;
  element.hidden = !message;
}

/** Friendly text for getUserMedia failures. */
export function microphoneErrorMessage(error) {
  switch (error?.name) {
    case 'NotAllowedError':
    case 'SecurityError':
      return 'Microphone permission was denied. Allow microphone access in your browser settings, then try again.';
    case 'NotFoundError':
    case 'OverconstrainedError':
      return 'No microphone was found. Connect a microphone or headset and try again.';
    case 'NotReadableError':
      return 'The microphone is in use by another application or could not be started.';
    default:
      return 'The microphone could not be started. Check your device and browser settings.';
  }
}
