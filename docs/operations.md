# Operations

## Hosting model

- One ASP.NET Core application, **exactly one instance**. All session state is in memory, so do not scale out or enable
  autoscaling: live sockets, the concurrency limit, the report queue, and the session store are process-local.
- An application restart or redeploy discards every in-progress session, transcript, and report. Users are told this in
  the UI; announce maintenance windows if needed.
- No database, storage account, or file share is required.

## Azure App Service deployment (Linux or Windows)

1. Create an App Service plan that supports WebSockets (B1 or higher) and a Web App for **.NET 10**.
2. In **Configuration → General settings**: turn **Web sockets** on, **Always on** on, **ARR affinity** on, and
   **HTTPS only** on. Keep the instance count at 1.
3. Add application settings (use Key Vault references for secrets):
   - `ASPNETCORE_ENVIRONMENT=Production`
   - `Ai__Mode=Azure`
   - `AccessGate__SharedCode=@Microsoft.KeyVault(SecretUri=...)`, `AccessGate__CodeVersion=1`
   - `AzureRealtime__*` and `AzureReports__*` (see [configuration.md](configuration.md))
   - `Security__UseForwardedHeaders=true` (App Service terminates TLS in front of the app)
   - Optional: `APPLICATIONINSIGHTS_CONNECTION_STRING`
4. For keyless auth, enable a system-assigned managed identity and grant it **Cognitive Services OpenAI User** on the
   Azure OpenAI / Foundry resource, then set `*__Authentication=EntraId`.
5. Deploy: `dotnet publish src/AdvisorCoachStudio.Web -c Release -o publish` and zip-deploy the folder
   (or use the GitHub Actions workflow as a starting point).
6. Health probes: `/health/live` (process) and `/health/ready` (application). Neither calls Azure AI or exposes
   configuration.

`DevelopmentSimulation` refuses to start outside `Development`, so a misconfigured production app fails fast instead of
serving simulated conversations.

## Rotating the access code

Change `AccessGate__SharedCode` and increment `AccessGate__CodeVersion`. Every previously issued cookie becomes invalid
immediately, including on active connections (checked every few seconds).

## Telemetry

Structured logs and (when configured) Azure Monitor metrics contain metadata only:

| Signal | Examples |
|---|---|
| Logs | correlation ID, session ID, end reason, duration, turn counts, interruptions, late/duplicate provider events, provider error type/code, report attempts and safe failure codes |
| Meter `AdvisorCoachStudio.Realtime` | `acs.realtime.connections`, `connection_failures`, `interruptions`, `provider_errors`, `transcripts_incomplete`, `session_seconds` |
| Meter `AdvisorCoachStudio.Reports` | `acs.reports.attempts`, `outcomes`, `latency_seconds` |

Never add transcript text, prompts, audio, keys, or tokens to logs, exceptions, or telemetry. Provider usage (token
counts) is logged at Debug level and is not an exact bill.

## Engineering budgets

| Metric | Target / policy |
|---|---|
| Connection setup timeout | 15 s |
| Typical readiness | < 5 s when the provider is healthy |
| Turn latency | p50 < 1.5 s, p95 < 3 s (end of advisor speech → audible output) |
| Local interruption reaction | playback stops within 250 ms of `playback.clear` |
| Transcript finalization drain | ≤ 5 s |
| Report generation | ≤ 60 s per attempt (configurable) |
| Roleplay duration | 2 min hard cap (configurable) |
| Concurrent live streams | 5 globally, 1 per browser access session |

These targets have **not** been measured against real Azure deployments yet. Record measurements, environment, and
sample size when you run the live checklist; mock-provider results do not count.

## Live Azure verification

Run explicitly after credentials are configured and record results separately from automated tests. Until every item
passes, describe the live integration as *implemented but not live-verified*.

Start with the provider-level live check, which uses your real deployments with no fakes:
[tools/AdvisorCoachStudio.LiveCheck](../tools/AdvisorCoachStudio.LiveCheck/README.md) (add `--report` to include the
report deployment). Then run the opt-in `LiveAzureTests` browser test against a running instance
([tests/browser](../tests/browser/README.md#live-azure-test-opt-in)) and the manual browser checklist.

Every session also verifies its system instructions at startup. Azure echoes the effective session in
`session.updated`; the relay compares the echoed instructions with the ones it sent and logs
`Provider confirmed the session instructions … {PromptVersion}, {Length} chars, sha256 {Fingerprint}`. If Azure
reports different instructions, setup fails (`provider_unavailable`) rather than running the wrong client persona.
The fingerprint matches the one the live check prints.

1. Authenticate and configure a real GA WebSocket session (check startup log readiness, then start a session).
2. Confirm microphone PCM reaches Azure, the client is audible, and your speech is transcribed.
3. Complete one conversation for each scenario.
4. Exercise mute, interruption (talk over the client), output truncation, and End.
5. Confirm no Azure key, token, endpoint secret, or prompt is visible in browser network traffic or page source
   (browser dev tools → Network → WS frames and JS/CSS assets).
6. Generate and validate a real coaching report with the configured report deployment.
7. Play a separate spoken demonstration and confirm the report does not change.
8. Measure latency using the definitions above (sample size ≥ 20 turns).
9. Force provider errors (wrong key, wrong deployment, network drop) and verify partial transcript preservation and
   clear user messages.

### Verification record

| Date | Scope | Result |
|---|---|---|
| 2026-11-08 | Live check, `gpt-realtime-2.1` deployment, `market-decline` / Intermediate, 3 synthetic advisor clips with an interruption | 23/23 checks passed. Instructions applied exactly (4343 chars); `whisper-1` transcription works without a separate deployment; opening spoken in character; facts revealed only when prompted; interruption cancelled and truncated the reply. Server-side time from VAD end-of-speech to first client audio: about 1.2–2.2 s (4 samples over 2 runs, not end-to-end latency). Items 1, 2 (provider side), and 4 (interruption and truncation) verified at provider level. |
| 2026-11-08 | Reports with a `gpt-5.1` deployment (live check `--report` and replays of a saved live transcript) | The first runs failed: every quote was rejected because `gpt-5.1` copied apostrophes, which the prompt JSON contained as `\u0027`/`\u2019` escapes, as U+0019. Fixed by sending the transcript with literal punctuation (prompt `coaching-report/1.0.1`) and by mapping quotes that differ only in punctuation style back to the exact transcript text before strict validation. Afterwards each report validated on the first request: 28 s (default effort) and 32 s (`low`), 24–29 quotes verified. Item 6 verified. |
| 2026-11-08 | `LiveAzureTests` browser runs against a local instance (Edge, fake microphone playing the 3 clips), 2 runs | Both passed: a real conversation through the UI (72–82 s, 10 turns), then a `gpt-5.1` report rendered with an overall score, six skill scores, and evidence links. The first run needed one correction request (practice exercise over 700 characters), so the prompt now states the length limits; the second run, with `ReasoningEffort=low`, produced a valid report in one 28 s request. Item 2 verified end to end with synthetic speech; items 3, 5, 7, 8, and 9 remain manual. |
| 2026-11-08 | Azure App Service deployment (`azd up`, B1 Linux, Canada East; Azure OpenAI in another subscription via an API key held in Key Vault) | Key Vault references resolved through the managed identity; startup log `AI mode: Azure. Realtime: Ready. Reports: Ready.`; `LiveAzureTests` against the deployed URL passed (sign-in, voice conversation over App Service WebSockets, `gpt-5.1` report). |

Server VAD (`silence_duration_ms` 500) splits speech at natural pauses between sentences. Azure starts a reply at
each pause and cancels it (`turn_detected`) when the advisor continues; in the live check these provisional replies
ended before producing any audio. If users hear the client start to talk during their pauses, raise
`Realtime__VadSilenceDurationMs` (for example to 700–800).

## Troubleshooting

| Symptom | Check |
|---|---|
| "Azure setup required" | Startup log lists missing setting names; verify `Ai__Mode=Azure` |
| `provider_auth_failed` | API key/role assignment, token scope, resource region |
| `provider_not_found` | Deployment name (not model name) in `AzureRealtime__Deployment` |
| No advisor captions | `AzureRealtime__TranscriptionModel` must be valid for your resource (`whisper-1` worked with a `gpt-realtime-2.1` deployment in the live check; otherwise it may need to be a deployment name) |
| Setup fails and the log says `Provider applied different session instructions` | Azure reported instructions other than the ones sent (for example, truncated). Compare the logged lengths and fingerprints with `--show-prompt` output from the live check |
| Report `setup_required` | `AzureReports__*` settings; deployment must support strict JSON-schema structured outputs |
| Report `invalid_report_output` | The log line `Report output … failed validation` lists each problem by field and turn label (never content). Quote problems usually mean the model altered text; length problems mean it ignored the stated limits. Retry, or use a stronger report deployment |
| Report `report_rejected` with a reasoning model | `AzureReports__ReasoningEffort` must be empty for models without reasoning, and set only to values the model supports |
| Reports slow or truncated (`finish length`) | Each request logs its duration and token counts; for reasoning models raise `AzureReports__MaxOutputTokens` and `AzureReports__TimeoutSeconds` (see [configuration](configuration.md#report-deployments)) |
| WebSocket 403 | `Origin` mismatch — set `Security__AllowedOrigins__0` to the public URL, or enable forwarded headers behind a proxy |
| Echo or feedback | Use headphones; browser echo cancellation is requested but not guaranteed |
