# Deploying to Azure App Service

The repository includes an [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/)
template (`azure.yaml` and `infra/`). `azd up` creates the Azure resources and deploys the web app.

## What gets created

| Resource | Purpose |
|---|---|
| Resource group `rg-<environment>` | Holds everything below |
| App Service plan (Linux, `B1` by default) | Compute. B1 is the smallest tier with Always On |
| Web app (.NET 10) | WebSockets on, Always On, HTTPS only, TLS 1.2+, FTP off, health check `/health/live`, system-assigned managed identity |
| Key Vault (RBAC) | Secrets `access-code` and `azure-openai-api-key`, read by the app through Key Vault references |
| Log Analytics and Application Insights | Logs, traces, and the app's realtime and report metrics |

Azure OpenAI is **not** created: point the app at existing deployments (a realtime deployment, and a chat deployment
with strict JSON-schema structured outputs for reports). The resource can be in another subscription or tenant when
you use an API key.

**One instance only.** Sessions live in memory (there is no database), so the app must run as a single instance. Do
not scale out. Restarts, deployments, and platform maintenance end active sessions; that is by design for the MVP.

## Prerequisites

- An Azure subscription where you can create resources and role assignments (Owner, or Contributor plus User Access
  Administrator).
- [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd) and the .NET 10 SDK.
- Azure OpenAI deployments, their endpoint, and (for API-key mode) a key.

## Deploy

```powershell
azd auth login            # or: az login, then: azd config set auth.useAzCliAuth true
azd env new advisor-coach-studio
azd env set AZURE_SUBSCRIPTION_ID <subscription-id>
azd env set AZURE_LOCATION canadacentral          # ideally the region of your Azure OpenAI resource

azd env set ACS_REALTIME_ENDPOINT https://<resource>.openai.azure.com
azd env set ACS_REALTIME_DEPLOYMENT <realtime-deployment>
azd env set ACS_REPORTS_DEPLOYMENT <report-deployment>
azd env set ACS_AI_API_KEY <azure-openai-key>     # stored in Key Vault by the deployment
azd env set ACS_ACCESS_CODE <long-random-code>    # at least 16 characters; stored in Key Vault

azd provision --preview   # review what will be created
azd up                    # provision and deploy; prints the app URL
```

Generate an access code with, for example, `openssl rand -base64 18`, or in PowerShell:
`[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(18))`.

Optional settings (`azd env set <name> <value>`):

| Setting | Default | Notes |
|---|---|---|
| `ACS_REPORTS_ENDPOINT` | realtime endpoint | When reports use a different resource (same key) |
| `ACS_TRANSCRIPTION_MODEL` | `whisper-1` | |
| `ACS_REALTIME_VOICE` | provider default | For example `alloy` |
| `ACS_REPORTS_REASONING_EFFORT` | empty | `low` for `gpt-5.1`; leave empty for non-reasoning models |
| `ACS_REPORTS_MAX_OUTPUT_TOKENS` | `4000` | `16000` for `gpt-5.1` |
| `ACS_REPORTS_TIMEOUT_SECONDS` | `60` | `120` for `gpt-5.1` |
| `ACS_APP_SERVICE_SKU` | `B1` | `P0v3` or larger for production |
| `ACS_AI_AUTHENTICATION` | `ApiKey` | `EntraId` for keyless access (see below) |
| `ACS_ACCESS_CODE_VERSION` | `1` | Change it to sign out every browser after changing the code |

`azd` keeps these values, including secrets, in `.azure/<environment>/.env` on your machine (ignored by git). The
deployed app reads secrets only from Key Vault.

## Keyless access with Microsoft Entra ID

When the Azure OpenAI resource is in the same tenant, you can avoid API keys: set `ACS_AI_AUTHENTICATION=EntraId`
(leave `ACS_AI_API_KEY` empty), run `azd up`, then give the web app's managed identity access to the Azure OpenAI
resource:

```powershell
$principal = az webapp identity show -g rg-<environment> -n <web-app-name> --query principalId -o tsv
az role assignment create --assignee-object-id $principal --assignee-principal-type ServicePrincipal `
  --role "Cognitive Services OpenAI User" --scope <azure-openai-resource-id>
```

## After deploying

- Open the printed URL (`azd env get-values` shows `WEB_URI`) and sign in with the access code.
- Startup log: `az webapp log tail -g rg-<environment> -n <web-app-name>` should show
  `AI mode: Azure. Realtime: Ready. Reports: Ready.`
- Run the opt-in live browser test against the deployment: set `ACS_LIVE_BASE_URL` to the app URL (see
  [tests/browser](../tests/browser/README.md#live-azure-test-opt-in)).

### Rotate secrets

```powershell
az keyvault secret set --vault-name <key-vault-name> --name access-code --value <new-code>
az keyvault secret set --vault-name <key-vault-name> --name azure-openai-api-key --value <new-key>
az webapp restart -g rg-<environment> -n <web-app-name>   # Key Vault references refresh on restart
```

To sign everyone out after changing the access code, also change the `AccessGate__CodeVersion` app setting (or set
`ACS_ACCESS_CODE_VERSION` and run `azd provision`). The deployment grants the deploying user Key Vault Secrets Officer
on the vault for these commands.

### Custom domains

WebSocket connections are accepted only from allowed origins. The template allows the default
`https://<app>.azurewebsites.net` host; add each custom domain as another app setting, for example
`Security__AllowedOrigins__1=https://coach.example.com`.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `SubscriptionIsOverQuotaForSku` (for example `Current Limit (B1 VMs): 0`) | New subscriptions can have zero App Service quota in some regions. Try another region (`azd env set AZURE_LOCATION canadaeast`; delete the empty `rg-<environment>` first if it was created in the old region), or request a quota increase in the portal (Quotas, App Service) |
| The app URL shows the default "Microsoft Azure App Service - Welcome" page after `azd up` | The container is still running the placeholder from provisioning. Restart it: `az webapp restart -g rg-<environment> -n <web-app-name>` (seen with older `azd` versions) |
| Sign-in page works but the conversation does not connect | WebSocket `Origin` mismatch: add the browser's origin to `Security__AllowedOrigins__n` (see Custom domains) |
| `Realtime: SetupRequired` or `Reports: SetupRequired` in the startup log | A setting is empty or a Key Vault reference did not resolve; check the app settings' Key Vault reference status in the portal (Environment variables) and restart after fixing |

## Cost

Roughly the App Service plan price (B1 is about US$13 per month; prices vary by region) plus Azure OpenAI usage per
conversation and report. Key Vault, Log Analytics, and Application Insights cost little at low volume.

## Remove everything

```powershell
azd down --purge   # deletes the resource group and purges the soft-deleted Key Vault
```
