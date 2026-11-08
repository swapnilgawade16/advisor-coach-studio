# Live check: real Azure, no fakes

`AdvisorCoachStudio.LiveCheck` talks to your **real** Azure GPT Realtime deployment using the application's own
connector, system-prompt builder, client event builders, and server event parser. It does not use the fake
provider or the simulator. Use it to verify a deployment before (or alongside) a manual browser test.

It checks that:

- the GA WebSocket endpoint accepts your credentials and `session.update`;
- Azure applied **exactly** the system instructions that were sent (compared with the `session.updated` echo);
- the effective session uses 24 kHz PCM16 in and out, server VAD with automatic replies and interruption, and the
  configured transcription model;
- the client speaks the scenario's opening line in character, as audio with a transcript;
- each advisor clip is transcribed and answered (VAD splits at pauses are reported, not failed);
- with `--interrupt`, talking over the client cancels its reply and `conversation.item.truncate` is acknowledged;
- every real server event parses with the application's strict GA parser;
- with `--report`, the configured `AzureReports` deployment turns the live transcript into a coaching report that passes
  the application's schema and evidence validation (every quote an exact substring of a quotable turn). Each request's
  duration and token usage are printed, plus Azure's error message if a request is rejected.

## Run it

The tool reads the same settings as the web app: `src/AdvisorCoachStudio.Web/appsettings.json`, the web project's
user secrets, and environment variables (`AzureRealtime__*`, `Realtime__*`). Keys are never printed.

Create advisor speech (24 kHz, 16-bit, mono WAV). On Windows you can synthesize it:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\AdvisorCoachStudio.LiveCheck\New-AdvisorSpeech.ps1 `
  -Text "I can hear how worrying this morning has been. When are you planning to retire?" -OutFile advisor-1.wav
```

Elsewhere, record or convert a clip: `ffmpeg -i input.wav -ar 24000 -ac 1 -c:a pcm_s16le advisor-1.wav`.

Then run:

```bash
dotnet run --project tools/AdvisorCoachStudio.LiveCheck -- --scenario market-decline --difficulty intermediate \
  --advisor-wav advisor-1.wav --advisor-wav advisor-2.wav --advisor-wav advisor-3.wav --interrupt --show-prompt
```

| Option | Meaning |
|---|---|
| `--scenario <id>` | `market-decline` (default), `fees-and-trust`, `first-time-investor` |
| `--difficulty <level>` | `beginner`, `intermediate` (default), `advanced` |
| `--advisor-wav <path>` | Advisor speech for one turn; repeat for several turns |
| `--interrupt` | Speak the last clip while the client's previous reply is still playing (needs two or more clips) |
| `--show-prompt` | Print the full system instructions sent in `session.update` |
| `--report` | Generate a coaching report from the live transcript with the `AzureReports` deployment |
| `--save-transcript <path>` | Save the live transcript as JSON for later report-only runs (contains conversation text) |
| `--transcript <path>` | Skip the voice conversation and generate a report from a saved transcript (implies `--report`) |
| `--dump-responses <dir>` | Write the raw report responses from Azure to a folder for inspection (contains report text) |

To tune report settings without repeating the voice conversation, save a transcript once and replay it. Settings can
be overridden per run with environment variables:

```powershell
dotnet run --project tools/AdvisorCoachStudio.LiveCheck -- --advisor-wav advisor-1.wav --advisor-wav advisor-2.wav --report --save-transcript live.json
$env:AzureReports__ReasoningEffort = "low"
dotnet run --project tools/AdvisorCoachStudio.LiveCheck -- --transcript live.json
```

Exit code `0` means every check passed, `1` means at least one check failed, and `2` means invalid arguments or
settings. A run uses roughly a minute of realtime audio on your deployment (plus one or two report requests with
`--report`), so it is billed like normal usage.

The output includes your Azure resource host name and the conversation transcripts. Review it before sharing.

## What it does not cover

Browser capture and playback, the application WebSocket protocol, report rendering, and end-to-end audible latency are
outside this tool. The opt-in `LiveAzureTests` browser test (see [tests/browser](../../tests/browser/README.md)) drives
a running instance through the real UI, and the manual checklist in
[docs/operations.md](../../docs/operations.md#live-azure-verification) covers the rest.
