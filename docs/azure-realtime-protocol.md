# Azure realtime protocol contract (GA)

This document records the Azure GPT Realtime **GA** WebSocket contract the server-side adapter
(`src/AdvisorCoachStudio.Web/Infrastructure/AzureRealtime`) implements. It was verified on 2026-11-07 against:

- [Use the GPT Realtime API via WebSockets](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-websockets)
- [Migration from Preview to GA version of Realtime API](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-preview-api-migration-guide)
- [Realtime audio reference](https://learn.microsoft.com/en-us/azure/foundry/openai/realtime-audio-reference), which links to the
  [OpenAI realtime reference](https://developers.openai.com/api/reference/resources/realtime)
- The generated OpenAI SDK type definitions (`openai-python`, `src/openai/types/realtime`) for exact field names

> One sample on the Azure WebSocket quickstart page ("send audio, receive audio") still uses preview-era flat session
> fields and event names such as `response.audio.delta`. This application deliberately uses only the GA shapes below.

## Connection

```text
wss://<resource-host>/openai/v1/realtime?model=<url-encoded-deployment-name>
```

- Built only from trusted server configuration (`AzureRealtime:Endpoint` must be an `https` resource root).
- `model` is the **deployment** name.
- No `api-version` query parameter and no `OpenAI-Beta` header.
- API key: `api-key: <key>` request header. Entra ID: `Authorization: Bearer <token>` with scope
  `https://ai.azure.com/.default` (configurable). Credentials are never placed in the URL.
- TLS certificate validation is always on; the connect timeout defaults to 15 s. Setup retries (bounded backoff) only
  happen before the session becomes active.

## Session configuration

Roleplay (`ProviderClientEvents.SessionUpdateRoleplay`):

```json
{
  "type": "session.update",
  "session": {
    "type": "realtime",
    "instructions": "<server-built roleplay prompt>",
    "output_modalities": ["audio"],
    "audio": {
      "input": {
        "format": { "type": "audio/pcm", "rate": 24000 },
        "transcription": { "model": "<AzureRealtime:TranscriptionModel>" },
        "turn_detection": {
          "type": "server_vad", "threshold": 0.5, "prefix_padding_ms": 300, "silence_duration_ms": 500,
          "create_response": true, "interrupt_response": true
        }
      },
      "output": { "format": { "type": "audio/pcm", "rate": 24000 }, "voice": "<AzureRealtime:Voice, optional>" }
    }
  }
}
```

Demonstration: same shape with `"turn_detection": null` and no input transcription (output only).

On Azure, `transcription.model` must name a transcription model available to (or deployed on) your resource. Voice and
transcription identifiers vary by region and deployment; verify them during live verification.

## Client events sent

| Event | When |
|---|---|
| `session.update` | Once, after `session.created`; the relay waits for `session.updated` before going active |
| `response.create` | Exactly once: the opening line (roleplay) or the demonstration. Later replies come from server VAD (`create_response: true`) |
| `input_audio_buffer.append` | Validated browser PCM16 frames (base64), bounded to 1 s queued |
| `input_audio_buffer.commit` | Only when ending while server VAD reported advisor speech (`speech_started`) that has not been committed yet, so the final utterance is transcribed. A manual commit never creates a response; a race with the VAD's own commit only yields the benign empty-buffer error |
| `response.cancel` | On interruption (if the response is still active) and when ending. A "no active response" error is benign |
| `conversation.item.truncate` | After an interruption, with `audio_end_ms` = the browser-reported played position (clamped to audio actually sent) |

## Server events handled

| Event | Use |
|---|---|
| `session.created`, `session.updated` | Handshake |
| `input_audio_buffer.speech_started` (`item_id`, `audio_start_ms`) | Advisor turn start; interruption trigger |
| `input_audio_buffer.speech_stopped`, `input_audio_buffer.committed` (`item_id`, `previous_item_id`) | Turn boundaries and conversation order |
| `conversation.item.added` (`previous_item_id`, `item`) | Authoritative item order (roles `user` → Advisor, `assistant` → Client) |
| `conversation.item.input_audio_transcription.delta/completed/failed` (`item_id`, `content_index`) | Advisor transcript (provisional, final, or failed) |
| `response.created`, `response.output_item.added` | Response/item tracking |
| `response.output_audio.delta/done` (`response_id`, `item_id`, `content_index`, `delta`) | Client audio (re-chunked to ≤200 ms and paced to the browser) |
| `response.output_audio_transcript.delta/done` | Client transcript |
| `response.done` (`status`: `completed`/`cancelled`/`failed`/`incomplete`, `status_details.reason`, `output`) | Final status; transcript fallback only when no `transcript.done` arrived (never concatenated) |
| `conversation.item.truncated` | Truncation acknowledgement |
| `error` (`error.type`, `error.code`) | Benign codes (`response_cancel_not_active`, `input_audio_buffer_commit_empty`) are ignored; authentication, rate-limit, quota, and server errors end the attempt and preserve completed turns |

Unknown events are ignored with metadata-only diagnostics. A known event missing a required field raises a provider
protocol error that ends the attempt visibly (rather than silently producing empty audio or transcripts). Legacy preview
names are **not** treated as known events.

## Coaching report API

Reports use a separate deployment through the versionless v1 API:

```text
POST https://<resource>/openai/v1/chat/completions
{ "model": "<deployment>", "messages": [...], "max_completion_tokens": 4000,
  "response_format": { "type": "json_schema", "json_schema": { "name": "coaching_report", "strict": true, "schema": { ... } } } }
```

Strict structured outputs do not support `maxLength`, `minItems`, `maxItems`, or `pattern`, so length and count bounds
are enforced by `ReportValidator` on the server. The checked-in schema is
`src/AdvisorCoachStudio.Web/Schemas/coaching-report-content.v1.schema.json`. Refusals (`message.refusal`),
`finish_reason` values `length`/`content_filter`, malformed JSON, and grounding failures are all handled explicitly.
HTTP 429 honours `retry-after-ms` and `Retry-After`.

## Synthetic fixtures

Synthetic GA event fixtures used by the protocol tests live in
`tests/AdvisorCoachStudio.UnitTests/Fixtures/ga-events/`. They contain no real conversations or credentials.
