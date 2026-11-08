// Resources for Advisor Coach Studio (deployed into the environment's resource group by main.bicep).

param location string
param tags object
param resourceToken string
param appServicePlanSku string
param realtimeEndpoint string
param realtimeDeployment string
param transcriptionModel string
param realtimeVoice string
param reportsEndpoint string
param reportsDeployment string
param reportsReasoningEffort string
param reportsMaxOutputTokens string
param reportsTimeoutSeconds string
param aiAuthentication string

@secure()
param aiApiKey string

@secure()
param accessCode string

param accessCodeVersion string
param principalId string

var useApiKey = aiAuthentication == 'ApiKey'
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
var keyVaultSecretsOfficer = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${resourceToken}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${resourceToken}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-${resourceToken}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

resource accessCodeSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: 'access-code'
  properties: {
    value: accessCode
    contentType: 'text/plain'
  }
}

resource apiKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (useApiKey) {
  parent: vault
  name: 'azure-openai-api-key'
  properties: {
    value: aiApiKey
    contentType: 'text/plain'
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${resourceToken}'
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: appServicePlanSku }
  properties: {
    reserved: true
  }
}

resource web 'Microsoft.Web/sites@2023-12-01' = {
  name: 'advisor-coach-${resourceToken}'
  location: location
  tags: union(tags, { 'azd-service-name': 'web' })
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      webSocketsEnabled: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health/live'
      numberOfWorkers: 1
    }
  }
}

// The app reads its secrets through Key Vault references with its managed identity.
resource webSecretsAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, web.id, keyVaultSecretsUser)
  properties: {
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
  }
}

// Lets the deploying user rotate the access code and API key later (az keyvault secret set).
resource deployerSecretsAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  scope: vault
  name: guid(vault.id, principalId, keyVaultSecretsOfficer)
  properties: {
    principalId: principalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficer)
  }
}

var apiKeyReference = useApiKey ? '@Microsoft.KeyVault(VaultName=${vault.name};SecretName=azure-openai-api-key)' : ''

resource settings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: web
  name: 'appsettings'
  dependsOn: [webSecretsAccess, apiKeySecret]
  properties: {
    ASPNETCORE_ENVIRONMENT: 'Production'
    // TLS ends at the App Service front end; trust its X-Forwarded-Proto so the app sees https requests.
    ASPNETCORE_FORWARDEDHEADERS_ENABLED: 'true'
    SCM_DO_BUILD_DURING_DEPLOYMENT: 'false'
    APPLICATIONINSIGHTS_CONNECTION_STRING: appInsights.properties.ConnectionString
    Ai__Mode: 'Azure'
    AccessGate__SharedCode: '@Microsoft.KeyVault(VaultName=${vault.name};SecretName=${accessCodeSecret.name})'
    AccessGate__CodeVersion: accessCodeVersion
    AzureRealtime__Endpoint: realtimeEndpoint
    AzureRealtime__Deployment: realtimeDeployment
    AzureRealtime__Authentication: aiAuthentication
    AzureRealtime__ApiKey: apiKeyReference
    AzureRealtime__TranscriptionModel: transcriptionModel
    AzureRealtime__Voice: realtimeVoice
    AzureReports__Endpoint: reportsEndpoint
    AzureReports__Deployment: reportsDeployment
    AzureReports__Authentication: aiAuthentication
    AzureReports__ApiKey: apiKeyReference
    AzureReports__ReasoningEffort: reportsReasoningEffort
    AzureReports__MaxOutputTokens: reportsMaxOutputTokens
    AzureReports__TimeoutSeconds: reportsTimeoutSeconds
    // WebSocket Origin allow-list. Add custom domains here (Security__AllowedOrigins__1, ...).
    Security__AllowedOrigins__0: 'https://${web.properties.defaultHostName}'
  }
}

resource webLogs 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: web
  name: 'logs'
  properties: {
    applicationLogs: { fileSystem: { level: 'Information' } }
    httpLogs: { fileSystem: { enabled: true, retentionInDays: 3, retentionInMb: 35 } }
    detailedErrorMessages: { enabled: false }
    failedRequestsTracing: { enabled: false }
  }
}

output webAppName string = web.name
output webUri string = 'https://${web.properties.defaultHostName}'
output keyVaultName string = vault.name
output webPrincipalId string = web.identity.principalId
