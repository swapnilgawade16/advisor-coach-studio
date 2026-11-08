# Application WebSocket protocol (`acs.v1`)

This is the **application** protocol between the browser and the Advisor Coach Studio backend. It is not the Azure
Realtime protocol: the backend translates between the two (see [azure-realtime-protocol.md](azure-realtime-protocol.md)).
The browser never talks to Azure directly and never sees Azure credentials, prompts, or raw provider events.

## Connection

| Purpose | URL | Subprotocol |
|---|---|---|
| Scored roleplay | `wss://<host>/ws/sessions/{sessionId}` | `acs.v1` |
| Output-only demonstration | `wss://<host>/ws/demonstrations/{demonstrationId}` | `acs.v1` |

The handshake must carry the access cookie and an allowed `Origin`. The server rejects the upgrade (401/403/404/409/429)
before contacting Azure when authentication, origin, ownership, state, or capacity checks fail.

All messages are UTF-8 JSON text frames with `"v": 1` and a `type`. Unknown types, missing fields, and wrong versions are
protocol errors.

## Audio format

* Mono, signed 16-bit little-endian PCM, 24,000 samples per second, base64-encoded inside JSON.
* Browser input frames: ~20 ms = 480 samples = 960 bytes per `audio.append` (small batches are tolerated).
* Server output chunks: at most 200 ms (9,600 bytes) per `audio.delta`.
* Raw PCM only: never WAV headers, Opus, or `MediaRecorder` output.

## Limits

| Limit | Value |
|---|---|
| Maximum assembled browser message | 32 KiB |
| Maximum control (non-audio) message | 8 KiB |
| Maximum decoded audio per `audio.append` | 24,000 bytes (500 ms) |
| Input throughput | real time plus a 2 s burst allowance (byte-accurate token bucket) |
| Audio queued towards Azure | at most 1 s (in practice one frame: audio is forwarded with backpressure, and a provider send stalled for more than 2 s ends the attempt with `connection_quality`) |
| Output audio released to the browser but not yet played | 2 s (server paces using `playback.progress`) |

Violations end the conversation with a clear `error` (`protocol_error`, `audio_rate_exceeded`, `connection_quality`).
Completed transcript turns are preserved.

## Browser → server

```json
{ "v": 1, "type": "audio.append", "clientSequence": 17, "audio": "<base64 PCM16>" }
```
`clientSequence` starts at 1 and must strictly increase. Only valid after `session.ready`. Not accepted on demonstration sockets.

```json
{ "v": 1, "type": "playback.progress", "responseId": "resp_1", "itemId": "item_1", "contentIndex": 0, "playedMilliseconds": 1240 }
```
Milliseconds of that item's audio actually rendered by the speakers' audio graph (not received or queued). Send every
100-250 ms while playing and once more when the item finishes. Treated as untrusted: the server clamps it to the audio it
sent and requires it to be monotonic per item.

```json
{ "v": 1, "type": "playback.stopped", "responseId": "resp_1", "itemId": "item_1", "contentIndex": 0, "playedMilliseconds": 980 }
```
Final played position after the browser executed `playback.clear` (interruption) for an item that had started playing.

```json
{ "v": 1, "type": "microphone.muted", "muted": true }
```
Informational. Muting is enforced in the browser by not sending samples.

```json
{ "v": 1, "type": "session.end", "reason": "user" }
```
Idempotent. The browser stops capture and playback immediately, then waits for `session.ended`.

```json
{ "v": 1, "type": "heartbeat" }
```
Every 10 s.

## Server → browser

| Type | Fields | Meaning |
|---|---|---|
| `session.state` | `state`: `connecting`, `ready`, `listening`, `client_speaking`, `interrupted`, `ending`, `ended` | Lifecycle and speaking state. `listening` is only sent after the backend has confirmed provider readiness. |
| `session.ready` | `sessionId`, `audio` (`encoding`, `sampleRate`, `channels`, `frameSamples`), `maxDurationSeconds`, `endsAtUtc`, `clientDisplayName`, `scenarioTitle`, `simulated` | Provider configured and acknowledged. Start streaming microphone audio now. |
| `audio.delta` | `seq`, `responseId`, `itemId`, `contentIndex`, `audio` | Client (or demonstrator) speech. `seq` strictly increases per connection. |
| `playback.clear` | `responseId`, `itemId` | Stop and discard queued output for that response immediately, then reply with `playback.stopped`. Ignore any later `audio.delta` for that `responseId`. |
| `transcript.partial` | `turnId`, `speaker`, `text` | Provisional text for a turn. Replace (do not append) any previous text for `turnId`. Render as provisional. |
| `transcript.final` | `turnId`, `sequence`, `speaker`, `text`, `status` (`final`, `failed`, `interrupted`, `pending`), `fullyHeard` | Final text for a turn; replaces the partial. `interrupted` client turns were not fully heard. `failed` means captions were unavailable. |
| `session.ended` | `reason`, `transcriptStatus` (`complete`, `incomplete`), `reportStatus`, `reportUrl` | The attempt is over. |
| `error` | `code`, `message`, `correlationId`, `retryable` | Safe error. Never contains provider bodies or secrets. |

`speaker` is `advisor` or `client` for roleplay sockets, and `demonstration` for demonstration sockets.

`reason` values: `user_ended`, `duration_limit`, `disconnected`, `provider_error`, `connection_quality`, `access_expired`.

## Interruption sequence

1. Azure reports `input_audio_buffer.speech_started` while client audio is playing.
2. Server sends `playback.clear` and `session.state: interrupted`; it ignores further audio for that response.
3. Browser stops output within one audio render quantum and replies with `playback.stopped` (actual played position).
4. Server cancels the response if still active and sends `conversation.item.truncate` with that played position.
5. The client turn is finalized with `status: interrupted`, `fullyHeard: false` and is excluded from report evidence.
