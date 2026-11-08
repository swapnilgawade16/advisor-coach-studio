# Advisor Coach Studio browser tests

End-to-end tests that drive the real application in a real Chromium-family browser with
[Playwright for .NET](https://playwright.dev/dotnet/) and xUnit v3. They cover the browser acceptance cases in
section 18.3 of `advisor_coach_studio_design.md`:

| Test class | What it checks |
|---|---|
| `AccessTests` | Wrong code shows the generic error; the right code opens the list of three scenarios; sign-out returns to `/access`. |
| `ScenarioSetupTests` | All three setup pages, Intermediate as the default difficulty, the chosen difficulty in the live panel; microphone permission denial keeps Start disabled. |
| `LiveSessionTests` | Simulation happy path: microphone check, `/practice/{id}` URL, connecting then speaking/listening states, one transcript item per turn (provisional then final), outbound `audio.append`, mute/unmute, End stops every track and clears playback, and the report page. |
| `ReloadTests` | Reloading or navigating away during a live session stops the microphone and ends the session; it cannot be rejoined. |
| `ReportStateTests` | Queued and generating states while the report model is gated; failed state with a retry that leads to a ready report. |
| `ReportEvidenceTests` | Limited evidence without an overall score when no advisor turn exists; a ready report with skill scores whose evidence links expand the transcript and focus the cited turn. |
| `DemonstrationTests` | The demonstration plays without requesting the microphone and leaves the roleplay report and transcript unchanged. |
| `KeyboardAndLayoutTests` | Setup and live controls work from the keyboard with a visible `:focus-visible` outline; no horizontal scrolling at 375x800. |
| `AudioFormatTests` | `wwwroot/js/audio-format.js` in the browser engine: PCM16 clamping, little-endian bytes, base64 round trips, and the streaming resampler (chunking invariance, output length, DC gain, 1 kHz amplitude). |
| `LiveAzureTests` | **Opt-in, real Azure.** Skipped unless the `ACS_LIVE_*` variables below are set. Drives an already running instance configured with real deployments: the microphone plays your advisor clips with pauses for the client's replies, the session is ended, and the real coaching report must render with six skill scores, evidence links, and the advisor's turns. Saves a full-page screenshot of the report. |

## Running locally

Prerequisites: the .NET SDK from `global.json` and Microsoft Edge (the default) or Google Chrome. No browser download
is needed for the default setup.

```powershell
dotnet test tests\browser
# Or with a separate output folder (useful when other builds run at the same time):
dotnet test tests\browser --artifacts-path <folder>
# A subset:
dotnet test tests\browser --filter "FullyQualifiedName~ReportStateTests"
```

Environment variables:

| Variable | Values |
|---|---|
| `ACS_BROWSER_CHANNEL` | unset or `msedge` (installed Edge), `chrome` (installed Chrome), `chromium` (Playwright's bundled Chromium; install it first, see below). |
| `ACS_BROWSER_HEADED` | `1` shows the browser windows (default: headless). |
| `ACS_BROWSER_TRACE_DIR` | Where failure traces and screenshots go (default: `playwright-traces` next to the test assembly). |

### Live Azure test (opt-in)

`LiveAzureTests` uses real Azure quota and needs a running instance in `Ai:Mode=Azure` (for example
`dotnet run --project src/AdvisorCoachStudio.Web` with your user secrets). Advisor clips must be 16-bit mono PCM WAV
files with one sample rate; on Windows, `tools/AdvisorCoachStudio.LiveCheck/New-AdvisorSpeech.ps1` synthesizes them.

| Variable | Value |
|---|---|
| `ACS_LIVE_BASE_URL` | The running instance, for example `http://127.0.0.1:5299` |
| `ACS_LIVE_ACCESS_CODE` | Its access code |
| `ACS_LIVE_ADVISOR_WAVS` | Semicolon-separated advisor clips, spoken in order (about 2-3 clips fit in the 2-minute cap) |
| `ACS_LIVE_OUTPUT_DIR` | Optional: where the composed microphone file and `live-report.png` go (default: `live-azure` next to the test assembly) |

```powershell
$env:ACS_LIVE_BASE_URL = "http://127.0.0.1:5299"; $env:ACS_LIVE_ACCESS_CODE = "<code>"
$env:ACS_LIVE_ADVISOR_WAVS = "advisor-1.wav;advisor-2.wav;advisor-3.wav"
dotnet test tests\browser --filter "FullyQualifiedName~LiveAzureTests"
```

To use the bundled Chromium locally, build once and install it with the script Playwright puts in the output folder:
`pwsh tests\browser\bin\Debug\net10.0\playwright.ps1 install chromium` (adjust the path when using `--artifacts-path`),
then set `ACS_BROWSER_CHANNEL=chromium`.

When a test fails, its output lists the browser console and the server log for that test, and a Playwright trace
(`.zip`) plus a full-page screenshot are saved. Open a trace with `pwsh <output>\playwright.ps1 show-trace <file>.zip`.

## Running in CI

`.github/workflows/ci.yml` builds the solution in Release, installs Chromium and its Linux dependencies with
`pwsh tests/browser/bin/Release/net10.0/playwright.ps1 install --with-deps chromium`, and runs
`dotnet test tests/browser --no-build -c Release` with `ACS_BROWSER_CHANNEL=chromium` (bundled Chromium in Playwright's
"new headless" mode). The suite needs no Azure credentials and makes no network calls outside the machine; the opt-in
live test skips itself because the `ACS_LIVE_*` variables are not set.

## How it works

**Application hosts.** `WebApplicationFactory<Program>` runs the real app on Kestrel at `http://127.0.0.1:{free port}`
(a secure context, so `getUserMedia` and AudioWorklets work without TLS) in the Development environment with
`AccessGate:SharedCode=browser-test-code`. Two hosts are shared by all tests:

* `SimulationAppHost`: `Ai:Mode=DevelopmentSimulation`, the product's in-memory realtime simulator and simulated reports.
* `AzureAppHost`: `Ai:Mode=Azure` with valid-looking fake Azure settings, the product's `SimulatedRealtimeConnector` as
  the realtime provider, and `ScriptedReportModelClient` as the report model. Each model call is parked on a
  `TaskCompletionSource` until the test completes or fails it. To show the queued state, two report jobs for unrelated
  sessions (created directly in the in-memory store) hold both report-worker slots at the model.

**Sign-in.** The access page allows ten attempts per IP address every five minutes, and every test connects from
127.0.0.1, so tests other than `AccessTests` receive an access cookie minted by the host with the application's own
cookie-authentication ticket format (same claims, scheme, and data protection as a real sign-in). Each browser context
is a separate anonymous owner.

**Microphones.** Each mode is a separate headless browser process, launched on first use with
`--use-fake-device-for-media-stream --autoplay-policy=no-user-gesture-required --mute-audio`:

* `FakeBeeps` (default, `--use-fake-ui-for-media-stream`): Chromium's fake device, a 20 ms beep every 500 ms. After
  Chromium's audio processing each beep is 3-4 loud 20 ms frames, which the simulator's energy gate treats as advisor
  speech that interrupts the client and never ends until the session ends. Tests do not depend on that detail.
* `SpeechBursts`: `--use-file-for-fake-audio-capture` with generated voice-like bursts, so each burst becomes a complete
  advisor turn (needed before the report model is called).
* `Silence`: a silent WAV, so the client's opening line plays to the end and no advisor turn exists.
* `PermissionDenied`: no fake-UI flag, no granted permission, and `--deny-permission-prompts`, so `getUserMedia` rejects
  with `NotAllowedError` instead of waiting on a prompt that headless mode cannot show.

**Instrumentation.** `Infrastructure/instrumentation.js` is added with `AddInitScriptAsync` and only observes: it counts
`getUserMedia` calls and keeps the returned streams (for `track.readyState`), records WebSocket messages sent and
received (counting `audio.append` and `audio.delta`) and close codes, records control messages posted to the capture and
playback AudioWorklets, records live-state transitions and transcript list updates with a MutationObserver, and saves
track and socket states at `pagehide` (after the product's own cleanup) so a test can check them after a reload.

Tests run one at a time (`DisableTestParallelization`): live sessions are real-time audio pipelines, and sequential
execution keeps them deterministic on small CI runners. Waits are on UI or page state, never fixed sleeps; the only
timed wait is the 1.5 s observation window that proves no audio is sent while muted.
