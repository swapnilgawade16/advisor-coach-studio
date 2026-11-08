// Pure, deterministic audio helpers shared by the main thread and the AudioWorklet scope.
// Wire format (protocol acs.v1): mono, signed 16-bit little-endian PCM, 24 kHz, base64 inside JSON.
// Nothing here touches window/document at module load, so worklets can import this module.

export const WIRE_SAMPLE_RATE = 24000;
export const FRAME_SAMPLES = 480;
export const FRAME_BYTES = FRAME_SAMPLES * 2;

const BASE64_CHUNK = 0x2000;
const MAX_TABLE_ENTRIES = 1 << 18;

/** Clamps float samples to [-1, 1] and converts them to PCM16 without overflow (NaN becomes 0). */
export function floatToPcm16(samples) {
  const length = samples.length;
  const out = new Int16Array(length);
  for (let i = 0; i < length; i++) {
    let s = samples[i];
    if (s > 1) s = 1;
    else if (s < -1) s = -1;
    else if (s !== s) s = 0;
    out[i] = s < 0 ? Math.round(s * 32768) : Math.round(s * 32767);
  }
  return out;
}

/** Converts PCM16 samples to floats in [-1, 1). */
export function pcm16ToFloat(samples) {
  const length = samples.length;
  const out = new Float32Array(length);
  for (let i = 0; i < length; i++) out[i] = samples[i] / 32768;
  return out;
}

function asUint8Array(bytes) {
  if (bytes instanceof Uint8Array) return bytes;
  if (bytes instanceof ArrayBuffer) return new Uint8Array(bytes);
  if (ArrayBuffer.isView(bytes)) return new Uint8Array(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  throw new TypeError('Expected a Uint8Array, ArrayBuffer, or ArrayBufferView.');
}

/** Serializes PCM16 samples as little-endian bytes, independent of platform endianness. */
export function int16ToLittleEndianBytes(samples) {
  const length = samples.length;
  const bytes = new Uint8Array(length * 2);
  const view = new DataView(bytes.buffer);
  for (let i = 0; i < length; i++) view.setInt16(i * 2, samples[i], true);
  return bytes;
}

/** Parses little-endian PCM16 bytes. Throws RangeError for an odd byte length. */
export function littleEndianBytesToInt16(bytes) {
  const u8 = asUint8Array(bytes);
  if (u8.byteLength % 2 !== 0) throw new RangeError('PCM16 data must contain an even number of bytes.');
  const view = new DataView(u8.buffer, u8.byteOffset, u8.byteLength);
  const out = new Int16Array(u8.byteLength / 2);
  for (let i = 0; i < out.length; i++) out[i] = view.getInt16(i * 2, true);
  return out;
}

/** Base64-encodes bytes (main thread only: uses btoa). */
export function bytesToBase64(bytes) {
  const u8 = asUint8Array(bytes);
  let binary = '';
  for (let i = 0; i < u8.length; i += BASE64_CHUNK) {
    binary += String.fromCharCode.apply(null, u8.subarray(i, i + BASE64_CHUNK));
  }
  return btoa(binary);
}

/** Decodes base64 into bytes (main thread only: uses atob). Throws on invalid input. */
export function base64ToBytes(base64) {
  if (typeof base64 !== 'string') throw new TypeError('Expected a base64 string.');
  const binary = atob(base64);
  const out = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) out[i] = binary.charCodeAt(i);
  return out;
}

/** Root-mean-square level of the samples (0 for empty input). */
export function computeRms(samples) {
  const length = samples ? samples.length : 0;
  if (length === 0) return 0;
  let sum = 0;
  for (let i = 0; i < length; i++) {
    const s = samples[i];
    sum += s * s;
  }
  return Math.sqrt(sum / length);
}

function gcd(a, b) {
  while (b !== 0) {
    const t = a % b;
    a = b;
    b = t;
  }
  return a;
}

// Blackman-windowed sinc. d is the distance in input samples, cutoff is in cycles per input sample.
function windowedSinc(d, cutoff, halfWidth) {
  if (d <= -halfWidth || d >= halfWidth) return 0;
  const x = 2 * cutoff * d;
  const sinc = x === 0 ? 1 : Math.sin(Math.PI * x) / (Math.PI * x);
  const w = d / halfWidth;
  const window = 0.42 + 0.5 * Math.cos(Math.PI * w) + 0.08 * Math.cos(2 * Math.PI * w);
  return 2 * cutoff * sinc * window;
}

/**
 * Streaming windowed-sinc sample-rate converter.
 *
 * Output sample n sits at input position n * inputRate / outputRate (tracked with exact integer
 * arithmetic), so splitting the input into arbitrary chunks yields bit-identical output to one call.
 * The low-pass cutoff is 0.95 * min(inputRate, outputRate) / 2, which anti-aliases when downsampling
 * and removes images when upsampling. Every polyphase row is normalized to unity DC gain.
 * The last `lookahead` input samples are held back until enough future context arrives.
 */
export class StreamingResampler {
  constructor(inputRate, outputRate, { halfTaps = 16 } = {}) {
    const inRate = Math.round(Number(inputRate));
    const outRate = Math.round(Number(outputRate));
    if (!(inRate > 0) || !(outRate > 0) || !Number.isFinite(inRate) || !Number.isFinite(outRate)) {
      throw new RangeError('Sample rates must be positive finite numbers.');
    }
    const taps = Math.floor(Number(halfTaps));
    if (!(taps >= 1 && taps <= 128)) throw new RangeError('halfTaps must be an integer between 1 and 128.');

    this._inputRate = inRate;
    this._outputRate = outRate;
    this._identity = inRate === outRate;
    this._half = 0;
    if (this._identity) return;

    const g = gcd(inRate, outRate);
    const p = inRate / g;
    const q = outRate / g;
    this._stepNum = p;
    this._stepDen = q;
    this._stepWhole = Math.floor(p / q);
    this._stepFrac = p % q;

    const ratio = Math.min(1, outRate / inRate);
    const cutoff = 0.475 * ratio;
    const halfWidth = taps / ratio;
    const half = Math.ceil(halfWidth);
    const width = 2 * half;
    const exact = q * width <= MAX_TABLE_ENTRIES;
    const phases = exact ? q : Math.max(16, Math.min(1024, Math.floor(MAX_TABLE_ENTRIES / width)));

    // Row r holds taps for fractional offset r / phases; tap j multiplies buffer[pos - half + 1 + j].
    const table = new Float64Array(phases * width);
    for (let r = 0; r < phases; r++) {
      const frac = r / phases;
      const base = r * width;
      let sum = 0;
      for (let j = 0; j < width; j++) {
        const v = windowedSinc(frac + half - 1 - j, cutoff, halfWidth);
        table[base + j] = v;
        sum += v;
      }
      const norm = 1 / sum;
      for (let j = 0; j < width; j++) table[base + j] *= norm;
    }

    this._half = half;
    this._width = width;
    this._table = table;
    this._phases = phases;
    this._exactPhases = exact;
    this._phaseScale = phases / q;
    this._buffer = new Float32Array(width + 2048);
    this.reset();
  }

  get inputRate() { return this._inputRate; }
  get outputRate() { return this._outputRate; }
  /** Input samples held back until more input (or padding) arrives. */
  get lookahead() { return this._half; }

  /** Clears history and fractional position; the next call starts a fresh stream. */
  reset() {
    if (this._identity) return;
    const history = this._half - 1;
    this._buffer.fill(0, 0, history);
    this._length = history;
    this._pos = history;
    this._num = 0;
  }

  /** Consumes input samples and returns every output sample that can now be computed. */
  process(input) {
    if (this._identity) return new Float32Array(input);
    const n = input.length;
    if (n > 0) {
      const needed = this._length + n;
      if (needed > this._buffer.length) {
        const grown = new Float32Array(Math.max(needed, this._buffer.length * 2));
        grown.set(this._buffer.subarray(0, this._length));
        this._buffer = grown;
      }
      this._buffer.set(input, this._length);
      this._length = needed;
    }

    const half = this._half;
    const available = this._length - 1 - half - this._pos;
    if (available < 0) return new Float32Array(0);

    const p = this._stepNum;
    const q = this._stepDen;
    // Outputs whose integer position stays within the available look-ahead.
    const count = Math.floor(((available + 1) * q - this._num + p - 1) / p);
    const out = new Float32Array(count);
    const buffer = this._buffer;
    const table = this._table;
    const width = this._width;
    const exact = this._exactPhases;
    const lastPhase = this._phases - 1;
    const scale = this._phaseScale;
    const stepWhole = this._stepWhole;
    const stepFrac = this._stepFrac;
    let pos = this._pos;
    let num = this._num;

    for (let o = 0; o < count; o++) {
      const phase = exact ? num : Math.min(lastPhase, Math.floor(num * scale));
      const t = phase * width;
      const s = pos - half + 1;
      let acc = 0;
      for (let j = 0; j < width; j++) acc += buffer[s + j] * table[t + j];
      out[o] = acc;
      pos += stepWhole;
      num += stepFrac;
      if (num >= q) {
        num -= q;
        pos += 1;
      }
    }

    // Keep only the history the next output still needs.
    const drop = Math.min(pos - half + 1, this._length);
    if (drop > 0) {
      buffer.copyWithin(0, drop, this._length);
      this._length -= drop;
      pos -= drop;
    }
    this._pos = pos;
    this._num = num;
    return out;
  }
}
