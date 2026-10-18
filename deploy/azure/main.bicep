// Loan Underwriting Orchestrator on Azure: App Service (API + React SPA), Azure SQL, Blob Storage, Azure OpenAI.
// All service-to-service access uses the web app's system-assigned managed identity: no keys or passwords.
targetScope = 'resourceGroup'

@description('Short prefix for resource names (3-12 lowercase letters/digits).')
@minLength(3)
@maxLength(12)
param namePrefix string
param location string = resourceGroup().location
@description('Azure OpenAI can live in a different region from the rest (model and quota availability differ by region).')
param openAiLocation string = location

@description('Entra ID user or group that administers the SQL server (needed to grant the web app database access).')
param sqlAdminLogin string
@description('Object id of the Entra ID admin above.')
param sqlAdminObjectId string

@description('Entra ID authority for API JWT validation, for example https://login.microsoftonline.com/<tenant>/v2.0')
param oidcAuthority string
param oidcAudience string = 'loan-underwriting-api'

@description('Create the Azure OpenAI account here. Set false when the subscription cannot create one; then pass externalModelEndpoint (or set Models__Azure__* on the web app later).')
param deployOpenAi bool = true
@description('Used when deployOpenAi is false, for example https://<resource>.openai.azure.com/openai/v1')
param externalModelEndpoint string = ''

@description('Chat model deployed to Azure OpenAI, used for every model role.')
param chatModelName string = 'gpt-4.1-mini'
param chatModelVersion string = '2025-04-14'
param embeddingModelName string = 'text-embedding-3-small'
param embeddingModelVersion string = '1'
param embeddingDimensions int = 1536
param appServiceSku string = 'B1'

var suffix = uniqueString(resourceGroup().id)
var planName = '${namePrefix}-plan'
var appName = '${namePrefix}-app-${suffix}'
var sqlName = '${namePrefix}-sql-${suffix}'
var dbName = 'LoanUnderwriting'
var storageName = toLower('${take(namePrefix, 8)}st${take(suffix, 10)}')
var openAiName = '${namePrefix}-aoai-${suffix}'
var logName = '${namePrefix}-log'
var aiName = '${namePrefix}-ai'

resource logs 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: aiName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'documents'
  properties: { publicAccess: 'None' }
}

resource sql 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlName
  location: location
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
  }
}

// Lets Azure services (the web app) reach the server. For production hardening replace with a Private Endpoint and VNet integration.
resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sql
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource db 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sql
  name: dbName
  location: location
  sku: {
    name: 'GP_S_Gen5_2'
    tier: 'GeneralPurpose'
  }
  properties: {
    autoPauseDelay: 60
    minCapacity: json('0.5')
  }
}

resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (deployOpenAi) {
  name: openAiName
  location: openAiLocation
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: openAiName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = if (deployOpenAi) {
  parent: openAi
  name: chatModelName
  sku: {
    name: 'GlobalStandard'
    capacity: 50
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: chatModelName
      version: chatModelVersion
    }
  }
}

// Deployments on one account must be created serially.
resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = if (deployOpenAi) {
  parent: openAi
  name: embeddingModelName
  dependsOn: [ chatDeployment ]
  sku: {
    name: 'GlobalStandard'
    capacity: 50
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: embeddingModelName
      version: embeddingModelVersion
    }
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: planName
  location: location
  kind: 'linux'
  sku: { name: appServiceSku }
  properties: { reserved: true }
}

resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      webSocketsEnabled: true // SignalR
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/api/health'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insights.properties.ConnectionString }
        { name: 'Deployment__Mode', value: 'Azure' }
        // Azure SQL with the web app's managed identity (deploy.ps1 creates the database user).
        { name: 'Database__ConnectionString', value: 'Server=tcp:${sql.properties.fullyQualifiedDomainName},1433;Database=${dbName};Authentication=Active Directory Managed Identity;Encrypt=True' }
        { name: 'Database__VectorProvider', value: 'Native' }
        { name: 'Database__AutoCreateSchema', value: 'true' }
        { name: 'Storage__Provider', value: 'Blob' }
        { name: 'Storage__BlobAccountUrl', value: storage.properties.primaryEndpoints.blob }
        { name: 'Storage__BlobContainer', value: container.name }
        { name: 'Models__Azure__Provider', value: 'OpenAiCompatible' }
        { name: 'Models__Azure__BaseUrl', value: deployOpenAi ? '${openAi.properties.endpoint}openai/v1' : externalModelEndpoint }
        { name: 'Models__Azure__AuthMode', value: 'AzureAd' }
        { name: 'Models__Azure__Models__Extraction', value: chatModelName }
        { name: 'Models__Azure__Models__Compliance', value: chatModelName }
        { name: 'Models__Azure__Models__Risk', value: chatModelName }
        { name: 'Models__Azure__Models__ScopeCheck', value: chatModelName }
        { name: 'Models__Azure__Models__QaAnswer', value: chatModelName }
        { name: 'Models__Azure__EmbeddingModel', value: embeddingModelName }
        { name: 'Models__Azure__EmbeddingDimensions', value: string(embeddingDimensions) }
        // No model endpoint yet: skip the startup model probe so the app can start.
        { name: 'SelfCheck__ProbeModels', value: string(deployOpenAi) }
        { name: 'Auth__Mode', value: 'Jwt' }
        { name: 'Auth__Authority', value: oidcAuthority }
        { name: 'Auth__Audience', value: oidcAudience }
        { name: 'Cors__Origins__0', value: 'https://${appName}.azurewebsites.net' }
      ]
    }
  }
}

// Role assignments for the web app's managed identity.
var blobContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' // Storage Blob Data Contributor
var openAiUser = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' // Cognitive Services OpenAI User

resource blobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, app.id, blobContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobContributor)
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource openAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployOpenAi) {
  scope: openAi
  name: guid(openAi.id, app.id, openAiUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUser)
    principalId: app.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output appName string = app.name
output appUrl string = 'https://${app.properties.defaultHostName}'
output sqlServer string = sql.properties.fullyQualifiedDomainName
output sqlDatabase string = dbName
