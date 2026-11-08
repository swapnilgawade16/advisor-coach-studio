# Configuration

Advisor Coach Studio uses standard ASP.NET Core configuration binding. Use `dotnet user-secrets` locally and
environment variables or Key Vault references when hosted. Environment variables use double underscores
(`AzureRealtime__ApiKey`). **Never commit secrets** — `appsettings.json` contains placeholders only.

## Operating modes (`Ai__Mode`)

| Mode | Behaviour |
|---|---|
| `Unconfigured` (default) | Scenario browsing and local microphone checks work. Live voice, reports, and demonstrations show **Azure setup required**. |
| `Azure` | Real server-to-Azure integration. A missing capability is reported explicitly (setup required, or report pending) and never replaced with fake data. |
| `DevelopmentSimulation` | Labelled "Simulated development data - not Azure". Only allowed when `ASPNETCORE_ENVIRONMENT=Development`; the app refuses to start otherwise. Never activated automatically after an Azure error. |

## Settings

| Setting (environment variable) | Purpose | Default / requirement |
|---|---|---|
| `Ai__Mode` | `Unconfigured`, `Azure`, or `DevelopmentSimulation` | `Unconfigured` |
| `AccessGate__SharedCode` | Shared access code (secret) | Required for anyone to enter |
| `AccessGate__CodeVersion` | Change to invalidate every issued access cookie (for example after rotating the code) | `1` |
| `AccessGate__CookieHours` | Anonymous access lifetime; in-memory sessions expire with it | `8` |
| `AzureRealtime__Endpoint` | HTTPS Azure resource root, e.g. `https://<resource>.openai.azure.com` (no path) | Required for live voice |
| `AzureRealtime__Deployment` | Realtime deployment name (may differ from the model name) | Required for live voice |
| `AzureRealtime__Authentication` | `ApiKey` or `EntraId` (explicit choice) | Required |
| `AzureRealtime__ApiKey` | Realtime API key (secret) | API-key mode only |
| `AzureRealtime__TranscriptionModel` | Input transcription model, or on Azure the transcription **deployment** name (e.g. `whisper-1`) | Required; verify against your resource |
| `AzureRealtime__Voice` | Output voice, e.g. `alloy` | Optional (provider default) |
| `AzureRealtime__TokenScope` | Entra token scope | `https://ai.azure.com/.default` |
| `AzureRealtime__ConnectTimeoutSeconds` | Connection timeout | `15` |
| `AzureReports__Endpoint` | Azure resource root for report generation | Required for reports |
| `AzureReports__Deployment` | Deployment that supports strict JSON-schema structured outputs | Required for reports |
| `AzureReports__Authentication` | `ApiKey` or `EntraId` | Required |
| `AzureReports__ApiKey` | Report API key (secret) | API-key mode only |
| `AzureReports__TokenScope` | Entra token scope | `https://ai.azure.com/.default` |
| `AzureReports__TimeoutSeconds` | Per-request deadline | `60` |
| `AzureReports__MaxAttempts` | Attempts for transient failures (throttling/network) | `3` |
| `AzureReports__MaxOutputTokens` | `max_completion_tokens` for the report; for reasoning models this includes reasoning tokens | `4000` |
| `AzureReports__ReasoningEffort` | `reasoning_effort` for reasoning-model deployments (`none`, `minimal`, `low`, `medium`, `high`, `xhigh`, as supported by the model). Leave empty for other models, which reject the parameter | empty (not sent) |
| `Realtime__MaxSessionMinutes` | Hard roleplay duration cap | `2` |
| `Realtime__MaxConcurrentSessions` | Process-wide live connections (roleplay + demonstrations) | `5` |
| `Realtime__VadThreshold` / `VadPrefixPaddingMs` / `VadSilenceDurationMs` | Server VAD tuning | `0.5` / `300` / `500` |
| `Realtime__FinalizationDrainSeconds` | Wait for outstanding transcription at the end | `5` |
| `Realtime__MaxQueuedInputMs` | Microphone audio queued towards Azure | `1000` |
| `Realtime__MaxQueuedOutputMs` | Client audio released to the browser but not yet played | `2000` |
| `Realtime__MaxBufferedResponseSeconds` | Server-side pacing buffer per response | `60` |
| `Realtime__DemonstrationMaxSeconds` | Demonstration cap | `90` |
| `Realtime__UnstartedSessionMinutes` | Created-but-never-connected sessions expire after | `10` |
| `Demonstration__Enabled` | Show the demonstration action when realtime is ready | `true` |
| `Security__AllowedOrigins__0..n` | Exact allowed WebSocket `Origin` values. Empty = same origin as the request | empty |
| `Security__UseForwardedHeaders` | Trust `X-Forwarded-*` (only behind a trusted proxy) | `false` |
| `Security__KnownProxies__0..n` | Trusted proxy IP addresses | empty |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Enables Azure Monitor OpenTelemetry export | unset |

There are intentionally **no** database, connection-string, or retention settings: the MVP keeps no persistent
storage.

The GA realtime connection has no `api-version` setting, and the report client uses the versionless Azure OpenAI v1
API (`/openai/v1/chat/completions`), so there is no `api-version` setting there either.

### Report deployments

The report deployment must support strict JSON-schema structured outputs through chat completions, for example
`gpt-4.1-mini`, `gpt-4.1`, or `gpt-5.1`. With reasoning models such as `gpt-5.1`, raise the output and time limits,
because reasoning tokens count toward `max_completion_tokens` and responses are longer:

| Setting | Suggested for `gpt-5.1` |
|---|---|
| `AzureReports__MaxOutputTokens` | `16000` |
| `AzureReports__TimeoutSeconds` | `120` |
| `AzureReports__ReasoningEffort` | `low` (or leave empty for the model default, `none` on `gpt-5.1`) |

Measured with `gpt-5.1` on a 10-turn live transcript: about 28 s with the default effort and about 32 s with `low`,
each in a single request. Every report request logs its duration and token counts (never content), so you can tune
these settings from the application log.

## Local secrets

```bash
cd src/AdvisorCoachStudio.Web
dotnet user-secrets set "AccessGate:SharedCode" "<choose-a-code>"
dotnet user-secrets set "Ai:Mode" "Azure"
dotnet user-secrets set "AzureRealtime:Endpoint" "https://<resource>.openai.azure.com"
dotnet user-secrets set "AzureRealtime:Deployment" "<realtime-deployment>"
dotnet user-secrets set "AzureRealtime:Authentication" "ApiKey"
dotnet user-secrets set "AzureRealtime:ApiKey" "<key>"
dotnet user-secrets set "AzureRealtime:TranscriptionModel" "<transcription-model-or-deployment>"
dotnet user-secrets set "AzureRealtime:Voice" "alloy"
dotnet user-secrets set "AzureReports:Endpoint" "https://<resource>.openai.azure.com"
dotnet user-secrets set "AzureReports:Deployment" "<report-deployment>"
dotnet user-secrets set "AzureReports:Authentication" "ApiKey"
dotnet user-secrets set "AzureReports:ApiKey" "<key>"
# Only for reasoning-model report deployments such as gpt-5.1:
dotnet user-secrets set "AzureReports:ReasoningEffort" "low"
dotnet user-secrets set "AzureReports:MaxOutputTokens" "16000"
dotnet user-secrets set "AzureReports:TimeoutSeconds" "120"
dotnet user-secrets list   # check names only; do not paste the output anywhere
```

## Microsoft Entra ID (keyless)

Set `AzureRealtime__Authentication=EntraId` (and/or `AzureReports__Authentication=EntraId`). The app uses
`DefaultAzureCredential` (Azure CLI login locally, managed identity when hosted) and requests a token for
`TokenScope`. Grant the identity the **Cognitive Services OpenAI User** role on the resource. Confirm the token audience
your resource accepts; Microsoft's current quickstarts use `https://ai.azure.com/.default`.

## Startup diagnostics

At startup the app logs the mode, readiness of each capability, and the **names** (never values) of missing or
invalid settings. `/api/capabilities` exposes only safe readiness flags to signed-in users.
