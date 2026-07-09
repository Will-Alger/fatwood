// ResearchDiscovery — minimal-but-real production footprint on Azure.
// Scope: resource group. Scaling up is a parameter change, not a rewrite.
//
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam \
//     -p pgAdminPassword=... adminApiKey=... anthropicApiKey=...
//
// Deliberate minimal-footprint tradeoffs (each has a documented upgrade path):
//   - Postgres uses a public endpoint restricted to Azure services; the
//     production upgrade is VNet integration + private endpoint.
//   - One consumption Container Apps environment, no zone redundancy.
//   - Secrets flow: Key Vault -> Container Apps secret refs via managed
//     identity. Nothing sensitive lives in this file or in app config.

@description('Region for all resources.')
param location string = 'eastus2'

@description('Short lowercase base name used to derive resource names.')
@minLength(3)
@maxLength(10)
param baseName string = 'rdisc'

@description('Container image for the API (and jobs). First deploy uses the public placeholder because ACR is empty until CD pushes; CD then pins real tags.')
param containerImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

// ---------------------------------------------------------------- secrets in
@secure()
@description('Postgres admin password. Re-supply on every infra deploy (it is re-asserted into Key Vault).')
param pgAdminPassword string

@secure()
@description('Admin API key protecting /api/admin/* surfaces. Re-supply on every infra deploy.')
param adminApiKey string

@secure()
@description('Anthropic API key for LLM steps. May be empty — the app runs without analysis until set.')
param anthropicApiKey string = ''

// ------------------------------------------------------------------- scaling
@description('Postgres compute SKU. Smallest burstable by default; e.g. Standard_D2ds_v5 for production.')
param pgSkuName string = 'Standard_B1ms'

@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param pgSkuTier string = 'Burstable'

@description('Postgres storage in GB (32 is the flexible-server minimum).')
param pgStorageGb int = 32

param pgVersion string = '16'
param pgAdminLogin string = 'research'
param pgDatabaseName string = 'researchdb'

@description('0 = scale to zero when idle (cold starts re-download the ~90 MB embedding model; set 1 to keep warm).')
param apiMinReplicas int = 0
param apiMaxReplicas int = 2

@description('vCPU per API replica, as a string for json() (e.g. \'0.5\', \'1\').')
param apiCpu string = '0.5'
param apiMemory string = '1Gi'

@allowed(['Basic', 'Standard', 'Premium'])
param acrSku string = 'Basic'

param logRetentionDays int = 30

@description('Daily delta-ingestion cron job. The in-process scheduler is disabled because the API scales to zero.')
param deployIngestJob bool = true

@description('Cron for the delta ingest job (UTC).')
param ingestCron string = '30 6 * * *'

// ---------------------------------------------------------------- resources
var suffix = uniqueString(resourceGroup().id)
var acrName = '${baseName}acr${suffix}' // alphanumeric only
// Key Vault names are capped at 24 chars; trim when a long baseName overflows.
var kvName = 'kv-${baseName}-${suffix}'
var kvNameSafe = length(kvName) > 24 ? substring(kvName, 0, 24) : kvName
var dbConnectionString = 'Host=${postgres.properties.fullyQualifiedDomainName};Port=5432;Database=${pgDatabaseName};Username=${pgAdminLogin};Password=${pgAdminPassword};Ssl Mode=Require'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${baseName}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: logRetentionDays
  }
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  sku: { name: acrSku }
  properties: {
    adminUserEnabled: false // pulls use managed identity; pushes use the CD principal
  }
}

// One user-assigned identity shared by the app and both jobs: pulls from ACR,
// reads secrets from Key Vault.
resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${baseName}-app-identity'
  location: location
}

var acrPullRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
var kvSecretsUserRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, appIdentity.id, acrPullRoleId)
  scope: acr
  properties: {
    principalId: appIdentity.properties.principalId
    roleDefinitionId: acrPullRoleId
    principalType: 'ServicePrincipal'
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvNameSafe
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: tenant().tenantId
    enableRbacAuthorization: true
    // Minimal footprint: soft delete on (mandatory); purge protection is
    // deliberately not enabled so a torn-down environment can be fully
    // reclaimed. Add enablePurgeProtection: true for production.
    enableSoftDelete: true
  }
}

resource kvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, appIdentity.id, kvSecretsUserRoleId)
  scope: keyVault
  properties: {
    principalId: appIdentity.properties.principalId
    roleDefinitionId: kvSecretsUserRoleId
    principalType: 'ServicePrincipal'
  }
}

resource secretDbConnection 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'db-connection-string'
  properties: { value: dbConnectionString }
}

resource secretAdminKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'admin-api-key'
  properties: { value: adminApiKey }
}

resource secretAnthropicKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'anthropic-api-key'
  properties: { value: anthropicApiKey }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: '${baseName}-pg-${suffix}'
  location: location
  sku: { name: pgSkuName, tier: pgSkuTier }
  properties: {
    version: pgVersion
    administratorLogin: pgAdminLogin
    administratorLoginPassword: pgAdminPassword
    storage: { storageSizeGB: pgStorageGb, autoGrow: 'Enabled' }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    network: { publicNetworkAccess: 'Enabled' }
  }

  resource db 'databases' = {
    name: pgDatabaseName
  }

  // Container Apps consumption workloads have no stable egress IP; this is the
  // documented "allow Azure services" rule. Production upgrade: VNet-integrate
  // the ACA environment and switch Postgres to a private endpoint.
  resource allowAzure 'firewallRules' = {
    name: 'AllowAllAzureServices'
    properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
  }
}

resource acaEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${baseName}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

// Shared shapes for the app and both jobs.
var kvSecrets = [
  {
    name: 'db-connection'
    keyVaultUrl: '${keyVault.properties.vaultUri}secrets/db-connection-string'
    identity: appIdentity.id
  }
  {
    name: 'admin-api-key'
    keyVaultUrl: '${keyVault.properties.vaultUri}secrets/admin-api-key'
    identity: appIdentity.id
  }
  {
    name: 'anthropic-api-key'
    keyVaultUrl: '${keyVault.properties.vaultUri}secrets/anthropic-api-key'
    identity: appIdentity.id
  }
]

var registries = [
  { server: acr.properties.loginServer, identity: appIdentity.id }
]

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${baseName}-api'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${appIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: acaEnv.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: registries
      secrets: kvSecrets
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: { cpu: json(apiCpu), memory: apiMemory }
          env: [
            { name: 'ConnectionStrings__Default', secretRef: 'db-connection' }
            { name: 'Admin__ApiKey', secretRef: 'admin-api-key' }
            { name: 'ANTHROPIC_API_KEY', secretRef: 'anthropic-api-key' }
            // Migrations run as a one-off job in CD, never at app startup.
            { name: 'Database__MigrateOnStartup', value: 'false' }
            // The in-process scheduler dies with scale-to-zero; the cron job
            // below owns the daily delta instead.
            { name: 'Ingestion__Schedule__Enabled', value: 'false' }
          ]
        }
      ]
      scale: {
        minReplicas: apiMinReplicas
        maxReplicas: apiMaxReplicas
        rules: [
          {
            name: 'http'
            http: { metadata: { concurrentRequests: '20' } }
          }
        ]
      }
    }
  }
  dependsOn: [acrPull, kvSecretsUser]
}

// One-off schema migration job: CD updates its image, starts it, and waits
// for success before rolling the app. Runs the EF migration bundle baked into
// the image (see Dockerfile), overriding the API entrypoint.
resource migrateJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${baseName}-migrate'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${appIdentity.id}': {} }
  }
  properties: {
    environmentId: acaEnv.id
    configuration: {
      triggerType: 'Manual'
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      replicaTimeout: 600
      replicaRetryLimit: 0
      registries: registries
      secrets: kvSecrets
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: containerImage
          command: ['/bin/sh', '-c', './efbundle --connection "$DB_CONNECTION"']
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'DB_CONNECTION', secretRef: 'db-connection' }
          ]
        }
      ]
    }
  }
  dependsOn: [acrPull, kvSecretsUser]
}

// Daily delta ingestion as an ACA cron job on the same image (the README's
// documented production evolution of the in-process scheduler). The image
// entrypoint is the API dll; args select the one-shot CLI mode.
resource ingestJob 'Microsoft.App/jobs@2024-03-01' = if (deployIngestJob) {
  name: '${baseName}-ingest-delta'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${appIdentity.id}': {} }
  }
  properties: {
    environmentId: acaEnv.id
    configuration: {
      triggerType: 'Schedule'
      scheduleTriggerConfig: {
        cronExpression: ingestCron
        parallelism: 1
        replicaCompletionCount: 1
      }
      // A delta is minutes, but a self-healing catch-up after downtime can be
      // long at arXiv's 1-req/3s etiquette.
      replicaTimeout: 3600
      replicaRetryLimit: 1
      registries: registries
      secrets: kvSecrets
    }
    template: {
      containers: [
        {
          name: 'ingest-delta'
          image: containerImage
          args: ['ingest', 'delta']
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'ConnectionStrings__Default', secretRef: 'db-connection' }
            { name: 'Database__MigrateOnStartup', value: 'false' }
          ]
        }
      ]
    }
  }
  dependsOn: [acrPull, kvSecretsUser]
}

output acrLoginServer string = acr.properties.loginServer
output acrName string = acr.name
output containerAppName string = api.name
output migrateJobName string = migrateJob.name
output ingestJobName string = deployIngestJob ? '${baseName}-ingest-delta' : ''
output keyVaultName string = keyVault.name
output postgresFqdn string = postgres.properties.fullyQualifiedDomainName
output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
