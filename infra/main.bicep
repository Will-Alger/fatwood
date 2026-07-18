// ResearchDiscovery — minimal-but-real production footprint on Azure.
// Scope: resource group. Scaling up is a parameter change, not a rewrite.
//
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam \
//     -p pgAdminPassword=... anthropicApiKey=...
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

@description('Container image for the API (and jobs). The bootstrap default is a public sample that, like the real app, listens on 8080 (the ingress target port — a port-80 placeholder never passes readiness); CD then pins real ACR tags.')
param containerImage string = 'mcr.microsoft.com/dotnet/samples:aspnetapp'

// ---------------------------------------------------------------- secrets in
@secure()
@description('Postgres admin password. Re-supply on every infra deploy (it is re-asserted into Key Vault).')
param pgAdminPassword string

@secure()
@description('Anthropic API key for LLM steps. May be empty — the app runs without analysis until set.')
param anthropicApiKey string = ''

// ------------------------------------------------- user accounts (JWT auth)
@description('Entra External ID authority (https://<tenant>.ciamlogin.com/<tenantId>/v2.0). Empty = user auth off; the API then runs open behind Easy Auth via an explicit transition flag.')
param userAuthAuthority string = ''

@description('Expected JWT audience: the Fatwood API app registration client id.')
param userAuthAudience string = ''

@description('Email promoted to Admin on first sign-in (bootstraps the first admin account).')
param bootstrapAdminEmail string = 'algerw@icloud.com'

@secure()
@description('Azure Communication Services connection string for branded OTP emails. Empty = Microsoft default emails.')
param acsConnectionString string = ''

@description('Auth-events app registration client id (audience of Entra custom-extension callbacks). Empty = hook disabled.')
param authEventsAudience string = ''

// NOTE: the Easy Auth (authConfigs) wall that fronted the whole site during
// the single-user era was RETIRED on 2026-07-12 — the app now does its own
// Entra External ID JWT auth (userAuthAuthority/userAuthAudience above) with
// anonymous browsing by design. Deliberately not declared here so an infra
// deploy can never re-erect a login wall over the public site.

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
param apiCpu string = '1'
param apiMemory string = '2Gi'

@allowed(['Basic', 'Standard', 'Premium'])
param acrSku string = 'Basic'

param logRetentionDays int = 30

@description('Daily delta-ingestion cron job. The in-process scheduler is disabled because the API scales to zero.')
param deployIngestJob bool = true

@description('Cron for the delta ingest job (UTC).')
param ingestCron string = '30 6 * * *'

@description('Durable analysis queue + KEDA-scaled worker job. When true the API enqueues to Azure Storage and the worker (not the web replica) drains it.')
param deployAnalysisQueue bool = true

@description('Name of the analysis work queue.')
param analysisQueueName string = 'analysis-jobs'

@description('Max concurrent analysis worker job executions KEDA may run. Kept low: executions × per-worker concurrency is the ceiling on simultaneous Anthropic calls.')
param analysisWorkerMaxParallel int = 2

@description('Papers a single worker analyzes at once.')
param analysisWorkerConcurrency int = 8

@description('Consecutive empty 1s polls before a worker execution exits. Long enough that bursts within a session reuse the warm worker instead of paying a fresh cold start.')
param analysisWorkerMaxIdlePolls int = 90

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

// Durable analysis queue. The API (enqueue) and the worker job (dequeue) reach
// it with the shared managed identity — no keys in config. Queue storage is
// billed per-operation and the messages are tiny/transient, so cost is cents.
var storageName = toLower(take(replace('${baseName}q${uniqueString(resourceGroup().id)}', '-', ''), 24))
var storageQueueDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')

resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' = if (deployAnalysisQueue) {
  name: storageName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
  }
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-01-01' = if (deployAnalysisQueue) {
  parent: storage
  name: 'default'
}

resource analysisQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-01-01' = if (deployAnalysisQueue) {
  parent: queueService
  name: analysisQueueName
}

resource storageQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAnalysisQueue) {
  name: guid(storage.id, appIdentity.id, storageQueueDataContributorRoleId)
  scope: storage
  properties: {
    principalId: appIdentity.properties.principalId
    roleDefinitionId: storageQueueDataContributorRoleId
    principalType: 'ServicePrincipal'
  }
}

// Packed search-index snapshots (embedding + lexical) live in a blob
// container on the same account: cold replicas download prebuilt indexes in
// seconds instead of rebuilding a 300k-paper corpus from the database.
var storageBlobDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = if (deployAnalysisQueue) {
  parent: storage
  name: 'default'
}

resource searchIndexContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = if (deployAnalysisQueue) {
  parent: blobService
  name: 'search-index'
}

resource storageBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAnalysisQueue) {
  name: guid(storage.id, appIdentity.id, storageBlobDataContributorRoleId)
  scope: storage
  properties: {
    principalId: appIdentity.properties.principalId
    roleDefinitionId: storageBlobDataContributorRoleId
    principalType: 'ServicePrincipal'
  }
}

var analysisQueueEndpoint = storage.?properties.primaryEndpoints.queue ?? ''
var searchIndexBlobEndpoint = storage.?properties.primaryEndpoints.blob ?? ''

// The KEDA queue-depth scaler on the worker JOB authenticates with a storage
// connection string (job scale rules take a secret, not a managed identity).
// The app's RUNTIME queue access stays managed-identity — only the scaler
// reads queue length via this secret, kept in Key Vault like the others.
resource secretStorageConnection 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = if (deployAnalysisQueue) {
  parent: keyVault
  name: 'storage-connection-string'
  properties: {
    value: 'DefaultEndpointsProtocol=https;AccountName=${storageName};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
  }
}

// Storage-queue env shared by the API (producer) and the worker job (consumer).
// AZURE_CLIENT_ID pins DefaultAzureCredential to the user-assigned identity.
var analysisQueueEnv = deployAnalysisQueue ? [
  { name: 'AnalysisQueue__UseStorageQueue', value: 'true' }
  { name: 'AnalysisQueue__AccountUrl', value: analysisQueueEndpoint }
  { name: 'AnalysisQueue__QueueName', value: analysisQueueName }
  { name: 'SearchIndex__AccountUrl', value: searchIndexBlobEndpoint }
  { name: 'AZURE_CLIENT_ID', value: appIdentity.properties.clientId }
] : []

resource secretDbConnection 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'db-connection-string'
  properties: { value: dbConnectionString }
}

resource secretAnthropicKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'anthropic-api-key'
  properties: { value: anthropicApiKey }
}

resource secretAcsConnection 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'acs-connection-string'
  properties: { value: acsConnectionString }
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
var kvSecrets = concat([
  {
    name: 'db-connection'
    keyVaultUrl: '${keyVault.properties.vaultUri}secrets/db-connection-string'
    identity: appIdentity.id
  }
  {
    name: 'anthropic-api-key'
    keyVaultUrl: '${keyVault.properties.vaultUri}secrets/anthropic-api-key'
    identity: appIdentity.id
  }
  {
    name: 'acs-connection-string'
    keyVaultUrl: '${keyVault.properties.vaultUri}secrets/acs-connection-string'
    identity: appIdentity.id
  }
], deployAnalysisQueue ? [
  {
    name: 'storage-connection'
    keyVaultUrl: '${keyVault.properties.vaultUri}secrets/storage-connection-string'
    identity: appIdentity.id
  }
] : [])

var registries = [
  { server: acr.properties.loginServer, identity: appIdentity.id }
]

var appSecrets = kvSecrets

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
      secrets: appSecrets
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: { cpu: json(apiCpu), memory: apiMemory }
          env: concat([
            { name: 'ConnectionStrings__Default', secretRef: 'db-connection' }
            { name: 'ANTHROPIC_API_KEY', secretRef: 'anthropic-api-key' }
            // User-account auth (Entra External ID JWT). While the authority
            // is empty the API runs open behind Easy Auth; the transition
            // flag acknowledges that deliberately instead of failing startup.
            { name: 'Auth__Authority', value: userAuthAuthority }
            { name: 'Auth__Audience', value: userAuthAudience }
            { name: 'Auth__DangerouslyAllowAnonymous', value: empty(userAuthAuthority) ? 'true' : 'false' }
            { name: 'Accounts__BootstrapAdminEmails__0', value: bootstrapAdminEmail }
            // Branded OTP verification email (Entra custom email provider →
            // our /api/auth-events/otp-send → ACS). Empty = Microsoft's email.
            { name: 'Email__AcsConnectionString', secretRef: 'acs-connection-string' }
            { name: 'AuthEvents__Audience', value: authEventsAudience }
            // Migrations run as a one-off job in CD, never at app startup.
            { name: 'Database__MigrateOnStartup', value: 'false' }
            // The in-process scheduler dies with scale-to-zero; the cron job
            // below owns the daily delta instead.
            { name: 'Ingestion__Schedule__Enabled', value: 'false' }
            // With the durable queue on, the API only ENQUEUES; the worker job
            // (below) drains it, so no in-process analysis worker runs here.
          ], analysisQueueEnv)
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
  // The secret resources are explicit dependencies: referencing only the
  // vault URI would let the app provision before the secrets exist, and the
  // ACA data plane resolves them at provision time.
  dependsOn: [acrPull, kvSecretsUser, secretDbConnection, secretAnthropicKey, secretAcsConnection]
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
  dependsOn: [acrPull, kvSecretsUser, secretDbConnection, secretAnthropicKey, secretAcsConnection]
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
      // A delta is minutes, but two things can be long: a self-healing
      // catch-up at arXiv's 1-req/3s etiquette, and a full corpus re-embed
      // after an embedding-model change (~19k papers on 0.5 vCPU; batches
      // persist, so a timeout only pauses progress — but 1h sliced the bge
      // migration into week-long pieces).
      replicaTimeout: 21600
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
          resources: { cpu: json('1'), memory: '2Gi' }
          env: [
            { name: 'ConnectionStrings__Default', secretRef: 'db-connection' }
            { name: 'Database__MigrateOnStartup', value: 'false' }
            // Post-ingest embed runs write fresh search-index snapshots so
            // API cold starts never rebuild the corpus from the database.
            { name: 'SearchIndex__AccountUrl', value: searchIndexBlobEndpoint }
            { name: 'AZURE_CLIENT_ID', value: appIdentity.properties.clientId }
          ]
        }
      ]
    }
  }
  dependsOn: [acrPull, kvSecretsUser, secretDbConnection, secretAnthropicKey, secretAcsConnection]
}

// Analysis worker: an event-driven job KEDA scales on queue depth. When work
// arrives it spins up (to analysisWorkerMaxParallel executions), each drains
// the queue with bounded intra-execution concurrency and exits when empty —
// scale-to-zero, so idle costs nothing. Survives web restarts (work persists
// in the queue) and processes multiple users' papers in parallel.
resource analyzeJob 'Microsoft.App/jobs@2024-03-01' = if (deployAnalysisQueue) {
  name: '${baseName}-analyze-worker'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${appIdentity.id}': {} }
  }
  properties: {
    environmentId: acaEnv.id
    configuration: {
      triggerType: 'Event'
      replicaTimeout: 1800
      replicaRetryLimit: 1
      eventTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
        scale: {
          minExecutions: 0
          maxExecutions: analysisWorkerMaxParallel
          // Poll briskly so the first analysis after idle isn't waiting up to a
          // full interval just to be noticed (cold start is the other half).
          pollingInterval: 10
          rules: [
            {
              name: 'queue-depth'
              type: 'azure-queue'
              metadata: {
                queueName: analysisQueueName
                queueLength: '1'
              }
              auth: [
                {
                  triggerParameter: 'connection'
                  secretRef: 'storage-connection'
                }
              ]
            }
          ]
        }
      }
      registries: registries
      secrets: kvSecrets
    }
    template: {
      containers: [
        {
          name: 'analyze-worker'
          image: containerImage
          args: ['analyze-worker']
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat([
            { name: 'ConnectionStrings__Default', secretRef: 'db-connection' }
            { name: 'ANTHROPIC_API_KEY', secretRef: 'anthropic-api-key' }
            { name: 'Database__MigrateOnStartup', value: 'false' }
            { name: 'AnalysisQueue__WorkerConcurrency', value: string(analysisWorkerConcurrency) }
            { name: 'AnalysisQueue__WorkerMaxIdlePolls', value: string(analysisWorkerMaxIdlePolls) }
          ], analysisQueueEnv)
        }
      ]
    }
  }
  dependsOn: [acrPull, kvSecretsUser, storageQueueRole, analysisQueue, secretDbConnection, secretAnthropicKey, secretStorageConnection]
}

output acrLoginServer string = acr.properties.loginServer
output acrName string = acr.name
output containerAppName string = api.name
output migrateJobName string = migrateJob.name
output ingestJobName string = deployIngestJob ? '${baseName}-ingest-delta' : ''
output analyzeJobName string = deployAnalysisQueue ? '${baseName}-analyze-worker' : ''
output keyVaultName string = keyVault.name
output postgresFqdn string = postgres.properties.fullyQualifiedDomainName
output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
