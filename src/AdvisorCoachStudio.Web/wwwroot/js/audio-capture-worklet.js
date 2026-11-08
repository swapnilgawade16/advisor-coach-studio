// AudioWorklet processor 'acs-capture': microphone -> mono -> 24 kHz -> 480-sample PCM16 LE frames.
import {
  FRAME_SAMPLES,
  StreamingResampler,
  WIRE_SAMPLE_RATE,
  floatToPcm16,
  int16ToLittleEndianBytes,
} from './audio-format.js';

const LEVEL_INTERVAL_SECONDS = 0.05;

function positiveInteger(value, fallback) {
  const n = Math.round(Number(value));
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

class AcsCaptureProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const opts = (options && options.processorOptions) || {};
    this._frameSamples = positiveInteger(opts.frameSamples, FRAME_SAMPLES);
    this._resampler = new StreamingResampler(sampleRate, positiveInteger(opts.targetSampleRate, WIRE_SAMPLE_RATE));
    this._frame = new Float32Array(this._frameSamples);
    this._frameFill = 0;
    this._mono = new Float32Array(128);
    this._muted = false;
    this._stopped = false;
    this._levelFrames = Math.max(1, Math.round(sampleRate * LEVEL_INTERVAL_SECONDS));
    this._levelSum = 0;
    this._levelCount = 0;
    this.port.onmessage = (event) => this._handleMessage(event.data);
  }

  _handleMessage(message) {
    if (this._stopped || !message || typeof message !== 'object') return;
    if (message.type === 'mute') {
      const muted = message.muted === true;
      if (muted === this._muted) return;
      this._muted = muted;
      // Drop the partial frame and filter history so no pre-mute audio is emitted after unmuting.
      this._frameFill = 0;
      this._resampler.reset();
      this._levelSum = 0;
      this._levelCount = 0;
    } else if (message.type === 'stop') {
      this._stopped = true;
      this._frameFill = 0;
      this._resampler.reset();
      this.port.onmessage = null;
    }
  }

  process(inputs) {
    if (this._stopped) return false;

    const input = inputs[0];
    const channelCount = input ? input.length : 0;
    if (channelCount === 0) {
      // No connected input this quantum: nothing to send, report silence.
      this._accumulateLevel(0, 128);
      return true;
    }

    const frames = input[0].length;
    if (this._muted) {
      this._accumulateLevel(0, frames);
      return true;
    }

    if (this._mono.length !== frames) this._mono = new Float32Array(frames);
    const mono = this._mono;
    let sumSquares = 0;
    if (channelCount === 1) {
      const channel = input[0];
      for (let i = 0; i < frames; i++) {
        const s = channel[i];
        mono[i] = s;
        sumSquares += s * s;
      }
    } else {
      const scale = 1 / channelCount;
      for (let i = 0; i < frames; i++) {
        let s = 0;
        for (let c = 0; c < channelCount; c++) s += input[c][i];
        s *= scale;
        mono[i] = s;
        sumSquares += s * s;
      }
    }

    this._accumulateLevel(sumSquares, frames);
    this._appendToFrame(this._resampler.process(mono));
    return true;
  }

  _appendToFrame(samples) {
    const total = samples.length;
    const frameSamples = this._frameSamples;
    let index = 0;
    while (index < total) {
      const take = Math.min(frameSamples - this._frameFill, total - index);
      this._frame.set(samples.subarray(index, index + take), this._frameFill);
      this._frameFill += take;
      index += take;
      if (this._frameFill === frameSamples) {
        const bytes = int16ToLittleEndianBytes(floatToPcm16(this._frame));
        this.port.postMessage({ type: 'frame', pcm: bytes.buffer }, [bytes.buffer]);
        this._frameFill = 0;
      }
    }
  }

  _accumulateLevel(sumSquares, frames) {
    this._levelSum += sumSquares;
    this._levelCount += frames;
    if (this._levelCount < this._levelFrames) return;
    const rms = this._muted ? 0 : Math.sqrt(this._levelSum / this._levelCount);
    this.port.postMessage({ type: 'level', rms });
    this._levelSum = 0;
    this._levelCount = 0;
  }
}

registerProcessor('acs-capture', AcsCaptureProcessor);
