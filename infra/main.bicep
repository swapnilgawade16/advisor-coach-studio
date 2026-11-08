targetScope = 'subscription'

// Advisor Coach Studio on Azure App Service. The app keeps all session state in memory, so it runs as a single,
// always-on instance. Azure OpenAI deployments are not created here: point the app at existing deployments.

@minLength(1)
@maxLength(40)
@description('Name of the azd environment. Used for the resource group name and to derive unique resource names.')
param environmentName string

@minLength(1)
@description('Azure region for all resources. Use the region of your Azure OpenAI resource for the lowest audio latency.')
param location string

@description('App Service plan SKU. B1 is the smallest tier with Always On; use P0v3 or larger for production use.')
param appServicePlanSku string = 'B1'

@description('Azure OpenAI resource root used for realtime voice, for example https://my-resource.openai.azure.com.')
param realtimeEndpoint string

@description('Realtime deployment name, for example gpt-realtime.')
param realtimeDeployment string

@description('Input transcription model or transcription deployment name.')
param transcriptionModel string = 'whisper-1'

@description('Optional output voice. Empty uses the provider default.')
param realtimeVoice string = ''

@description('Azure OpenAI resource root used for coaching reports. Empty uses the realtime endpoint.')
param reportsEndpoint string = ''

@description('Chat deployment with strict JSON-schema structured outputs, for example gpt-4.1-mini or gpt-5.1.')
param reportsDeployment string

@description('reasoning_effort for reasoning-model report deployments (for example low). Empty for other models.')
param reportsReasoningEffort string = ''

@description('max_completion_tokens for a report (includes reasoning tokens for reasoning models).')
param reportsMaxOutputTokens string = '4000'

@description('Deadline per report request, in seconds.')
param reportsTimeoutSeconds string = '60'

@allowed(['ApiKey', 'EntraId'])
@description('How the app authenticates to Azure OpenAI. EntraId uses the web app managed identity, which needs the Cognitive Services OpenAI User role on the Azure OpenAI resource (assign it after provisioning).')
param aiAuthentication string = 'ApiKey'

@secure()
@description('Azure OpenAI API key (ApiKey mode). Stored in Key Vault, never in app settings.')
param aiApiKey string = ''

@secure()
@minLength(16)
@description('Shared access code for the sign-in page. Stored in Key Vault. Use a long random value.')
param accessCode string

@description('Changing this value signs out every browser that used a previous access code.')
param accessCodeVersion string = '1'

@description('Object ID of the deploying user, granted Key Vault Secrets Officer so secrets can be rotated. Set by azd.')
param principalId string = ''

var tags = {
  'azd-env-name': environmentName
  application: 'advisor-coach-studio'
}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module resources 'resources.bicep' = {
  name: 'advisor-coach-studio-resources'
  scope: resourceGroup
  params: {
    location: location
    tags: tags
    resourceToken: toLower(uniqueString(subscription().id, environmentName, location))
    appServicePlanSku: appServicePlanSku
    realtimeEndpoint: realtimeEndpoint
    realtimeDeployment: realtimeDeployment
    transcriptionModel: transcriptionModel
    realtimeVoice: realtimeVoice
    reportsEndpoint: empty(reportsEndpoint) ? realtimeEndpoint : reportsEndpoint
    reportsDeployment: reportsDeployment
    reportsReasoningEffort: reportsReasoningEffort
    reportsMaxOutputTokens: reportsMaxOutputTokens
    reportsTimeoutSeconds: reportsTimeoutSeconds
    aiAuthentication: aiAuthentication
    aiApiKey: aiApiKey
    accessCode: accessCode
    accessCodeVersion: accessCodeVersion
    principalId: principalId
  }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = resourceGroup.name
output WEB_APP_NAME string = resources.outputs.webAppName
output WEB_URI string = resources.outputs.webUri
output KEY_VAULT_NAME string = resources.outputs.keyVaultName
