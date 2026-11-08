// AudioWorklet processor 'acs-playback': bounded queue of 24 kHz PCM16 segments, resampled to the
// context rate while rendering, with per-item played-position accounting for playback.progress.
import { StreamingResampler, WIRE_SAMPLE_RATE, littleEndianBytesToInt16, pcm16ToFloat } from './audio-format.js';

const PROGRESS_INTERVAL_SECONDS = 0.1;
const FEED_BLOCK_SAMPLES = 128;
const MAX_TRACKED_ITEMS = 64;

function positiveNumber(value, fallback) {
  const n = Number(value);
  return Number.isFinite(n) && n > 0 ? n : fallback;
}

class AcsPlaybackProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const opts = (options && options.processorOptions) || {};
    this._resampler = new StreamingResampler(positiveNumber(opts.inputSampleRate, WIRE_SAMPLE_RATE), sampleRate);
    this._inputRate = this._resampler.inputRate;
    this._outputRate = this._resampler.outputRate;
    this._maxBufferedSamples = Math.floor((positiveNumber(opts.maxBufferedMs, 3000) * this._inputRate) / 1000);
    this._padding = new Float32Array(this._resampler.lookahead);

    // Input side: segments not yet pushed through the resampler ({ item, samples, offset }).
    this._queue = [];
    this._queuedSamples = 0;
    // Pipeline: pushed input spans awaiting emission ({ item, end }) and resampled runs awaiting render
    // ({ item, data, offset, end }). Positions are counted from the last pipeline reset.
    this._spans = [];
    this._ahead = [];
    this._aheadSamples = 0;
    this._pushed = 0;
    this._emitted = 0;
    this._lastPushedItem = null;
    this._flushed = true;

    this._items = new Map();
    this._currentItem = null;
    this._playing = false;
    this._starving = false;
    this._underruns = 0;
    this._progressFrames = Math.max(1, Math.round(sampleRate * PROGRESS_INTERVAL_SECONDS));
    this._framesSinceProgress = 0;
    this._stopped = false;
    this.port.onmessage = (event) => this._handleMessage(event.data);
  }

  _handleMessage(message) {
    if (this._stopped || !message || typeof message !== 'object') return;
    switch (message.type) {
      case 'enqueue':
        this._enqueue(message);
        break;
      case 'complete':
        this._complete(message);
        break;
      case 'clear':
        this._clear(message);
        break;
      case 'stop':
        this._stop();
        break;
      default:
        break;
    }
  }

  _item(responseId, itemId, contentIndex, create) {
    const key = `${responseId}\u0000${itemId}\u0000${contentIndex}`;
    let item = this._items.get(key);
    if (!item && create) {
      item = {
        responseId,
        itemId,
        contentIndex,
        received: 0, // 24 kHz samples accepted
        queued: 0, // 24 kHz samples not yet pushed through the resampler
        spans: 0, // pipeline spans whose output has not been fully emitted
        pending: 0, // output-rate samples emitted but not yet rendered
        rendered: 0, // output-rate samples rendered
        complete: false,
        done: false,
        truncated: false,
        postedMs: -1,
        postedDone: false,
      };
      this._items.set(key, item);
      this._pruneItems();
    }
    return item;
  }

  _enqueue(message) {
    const { responseId, itemId, pcm } = message;
    if (typeof responseId !== 'string' || typeof itemId !== 'string' || !(pcm instanceof ArrayBuffer)) return;
    const contentIndex = Number.isInteger(message.contentIndex) ? message.contentIndex : 0;
    let samples;
    try {
      samples = pcm16ToFloat(littleEndianBytesToInt16(new Uint8Array(pcm)));
    } catch {
      return;
    }
    if (samples.length === 0) return;

    const item = this._item(responseId, itemId, contentIndex, true);
    const buffered = this._queuedSamples + Math.ceil((this._aheadSamples * this._inputRate) / this._outputRate);
    if (buffered + samples.length > this._maxBufferedSamples) {
      this.port.postMessage({
        type: 'overflow',
        responseId,
        itemId,
        contentIndex,
        droppedMs: Math.round((samples.length * 1000) / this._inputRate),
      });
      return;
    }

    // More audio means the item is still streaming, even if it was marked complete early.
    item.complete = false;
    item.done = false;
    item.postedDone = false;
    item.received += samples.length;
    item.queued += samples.length;
    this._queue.push({ item, samples, offset: 0 });
    this._queuedSamples += samples.length;
  }

  _complete(message) {
    const contentIndex = Number.isInteger(message.contentIndex) ? message.contentIndex : null;
    for (const item of this._items.values()) {
      if (
        item.responseId === message.responseId &&
        item.itemId === message.itemId &&
        (contentIndex === null || item.contentIndex === contentIndex)
      ) {
        item.complete = true;
      }
    }
  }

  _clear(message) {
    const responseId = typeof message.responseId === 'string' ? message.responseId : null;
    const matches = (item) => item !== null && (responseId === null || item.responseId === responseId);

    if (this._queue.length > 0) {
      const kept = [];
      for (const segment of this._queue) {
        if (matches(segment.item)) {
          const remaining = segment.samples.length - segment.offset;
          segment.item.queued -= remaining;
          this._queuedSamples -= remaining;
        } else {
          kept.push(segment);
        }
      }
      this._queue = kept;
    }

    // Audio already inside the resampling pipeline (a few ms) is discarded wholesale.
    if (this._spans.some((span) => matches(span.item)) || this._ahead.some((run) => matches(run.item))) {
      this._resetPipeline();
    }

    const items = [];
    for (const [key, item] of this._items) {
      if (!matches(item)) continue;
      if (item.rendered > 0) {
        items.push({
          responseId: item.responseId,
          itemId: item.itemId,
          contentIndex: item.contentIndex,
          playedMs: this._playedMs(item),
        });
      }
      this._items.delete(key);
    }
    if (this._currentItem && matches(this._currentItem)) this._currentItem = null;
    if (this._lastPushedItem && matches(this._lastPushedItem)) {
      this._lastPushedItem = null;
      this._flushed = true;
    }
    this._starving = false;

    const reply = { type: 'cleared', items };
    if (message.requestId !== undefined) reply.requestId = message.requestId;
    this.port.postMessage(reply);
  }

  _stop() {
    this._stopped = true;
    this._queue = [];
    this._queuedSamples = 0;
    this._resetPipeline();
    this._items.clear();
    this._currentItem = null;
    this.port.onmessage = null;
  }

  _resetPipeline() {
    for (const run of this._ahead) {
      if (run.item) {
        run.item.pending -= run.end - run.offset;
        run.item.truncated = true;
      }
    }
    for (const span of this._spans) {
      if (span.item) {
        span.item.spans -= 1;
        span.item.truncated = true;
      }
    }
    this._ahead = [];
    this._aheadSamples = 0;
    this._spans = [];
    this._pushed = 0;
    this._emitted = 0;
    this._lastPushedItem = null;
    this._flushed = true;
    this._resampler.reset();
  }

  process(inputs, outputs) {
    if (this._stopped) return false;
    const output = outputs[0];
    if (!output || output.length === 0) return true;

    const channel = output[0];
    const frames = channel.length;
    let written = 0;
    let audible = false;
    while (written < frames) {
      const run = this._ahead[0];
      if (!run) {
        if (!this._feed()) break;
        continue;
      }
      const n = Math.min(run.end - run.offset, frames - written);
      const data = run.data;
      for (let i = 0, src = run.offset; i < n; i++, src++) channel[written + i] = data[src];
      run.offset += n;
      written += n;
      this._aheadSamples -= n;
      if (run.item) {
        run.item.pending -= n;
        run.item.rendered += n;
        this._currentItem = run.item;
        audible = true;
      }
      if (run.offset === run.end) this._ahead.shift();
    }

    if (written < frames) channel.fill(0, written);
    for (let c = 1; c < output.length; c++) output[c].set(channel);
    this._afterRender(frames, written < frames, audible);
    return true;
  }

  // Pushes the next block of input (or tail padding) through the resampler. Returns false when idle.
  _feed() {
    let input;
    let item;
    const segment = this._queue[0];
    if (segment) {
      item = segment.item;
      const end = Math.min(segment.samples.length, segment.offset + FEED_BLOCK_SAMPLES);
      const count = end - segment.offset;
      input = segment.samples.subarray(segment.offset, end);
      segment.offset = end;
      item.queued -= count;
      this._queuedSamples -= count;
      if (end === segment.samples.length) this._queue.shift();
      this._lastPushedItem = item;
      this._flushed = false;
    } else if (!this._flushed && this._lastPushedItem && this._lastPushedItem.complete) {
      // Nothing more will arrive for the last item: pad with silence so the filter releases its tail.
      this._flushed = true;
      if (this._padding.length === 0) return false;
      input = this._padding;
      item = null;
    } else {
      return false;
    }

    this._pushed += input.length;
    const last = this._spans[this._spans.length - 1];
    if (last && last.item === item) {
      last.end = this._pushed;
    } else {
      this._spans.push({ item, end: this._pushed });
      if (item) item.spans += 1;
    }
    this._attribute(this._resampler.process(input));
    return true;
  }

  // Index of the first output sample whose input position is at or beyond inputEnd.
  _outputEnd(inputEnd) {
    return Math.floor((inputEnd * this._outputRate + this._inputRate - 1) / this._inputRate);
  }

  // Output n corresponds to input position n * inputRate / outputRate; attribute it to the span covering it.
  _attribute(out) {
    const total = out.length;
    let index = 0;
    let emitted = this._emitted;
    for (;;) {
      while (this._spans.length > 0 && emitted >= this._outputEnd(this._spans[0].end)) {
        const finished = this._spans.shift();
        if (finished.item) finished.item.spans -= 1;
      }
      if (index >= total) break;
      const span = this._spans[0];
      const limit = span ? Math.min(total, index + this._outputEnd(span.end) - emitted) : total;
      const item = span ? span.item : null;
      this._ahead.push({ item, data: out, offset: index, end: limit });
      this._aheadSamples += limit - index;
      if (item) item.pending += limit - index;
      emitted += limit - index;
      index = limit;
    }
    this._emitted = emitted;
  }

  _afterRender(frames, ranDry, audible) {
    let urgent = false;
    if (!ranDry) {
      this._starving = false;
    } else if (!this._starving && this._currentItem && !this._currentItem.complete) {
      // The queue ran dry while the playing item still expects more audio.
      this._starving = true;
      this._underruns += 1;
      this.port.postMessage({ type: 'underrun', count: this._underruns });
      urgent = true;
    }

    if (audible !== this._playing) {
      this._playing = audible;
      this.port.postMessage({ type: 'playing', playing: audible });
    }

    let finished = false;
    for (const item of this._items.values()) {
      if (!item.done && item.complete && item.queued === 0 && item.spans === 0 && item.pending === 0) {
        item.done = true;
        finished = true;
      }
    }

    this._framesSinceProgress += frames;
    if (finished || urgent || this._framesSinceProgress >= this._progressFrames) this._postProgress();
  }

  _postProgress() {
    const items = [];
    for (const item of this._items.values()) {
      if (item.rendered === 0 && !item.done) continue;
      const playedMs = this._playedMs(item);
      if (playedMs === item.postedMs && item.done === item.postedDone) continue;
      item.postedMs = playedMs;
      item.postedDone = item.done;
      items.push({
        responseId: item.responseId,
        itemId: item.itemId,
        contentIndex: item.contentIndex,
        playedMs,
        done: item.done,
      });
    }
    if (items.length === 0) return;
    this._framesSinceProgress = 0;
    this.port.postMessage({ type: 'progress', items });
  }

  // Milliseconds of the item's 24 kHz audio actually rendered, never more than was received.
  _playedMs(item) {
    const played =
      item.done && !item.truncated
        ? item.received
        : Math.min(item.received, Math.floor((item.rendered * this._inputRate) / this._outputRate));
    return Math.floor((played * 1000) / this._inputRate);
  }

  _pruneItems() {
    if (this._items.size <= MAX_TRACKED_ITEMS) return;
    for (const [key, item] of this._items) {
      if (item.done) this._items.delete(key);
      if (this._items.size <= MAX_TRACKED_ITEMS) break;
    }
  }
}

registerProcessor('acs-playback', AcsPlaybackProcessor);
