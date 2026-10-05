// Minimal-footprint values. Bumping to production scale is edits here
// (pgSkuName/pgSkuTier, apiMinReplicas, apiCpu/apiMemory, acrSku), not
// template changes. Secrets are passed on the command line, never stored:
//
//   az deployment group create -g <rg> -f infra/main.bicep -p infra/main.bicepparam \
//     -p pgAdminPassword=... adminApiKey=... anthropicApiKey=...
using 'main.bicep'

param location = 'eastus2'
param baseName = 'rdisc'

// Postgres: smallest burstable tier. Production: Standard_D2ds_v5 / GeneralPurpose.
param pgSkuName = 'Standard_B1ms'
param pgSkuTier = 'Burstable'
param pgStorageGb = 32

// API: one warm replica 07:00–23:00 US Eastern, scale to zero when idle
// overnight. Always-on around the clock: apiMinReplicas 1.
param apiMinReplicas = 0
param apiMaxReplicas = 2
param apiDaytimeReplicas = 1
param apiDaytimeTimezone = 'America/New_York'
param apiDaytimeStart = '0 7 * * *'
param apiDaytimeEnd = '0 23 * * *'
param apiCpu = '0.5'
param apiMemory = '1Gi'

param acrSku = 'Basic'
param logRetentionDays = 30

// Daily arXiv delta at 06:30 UTC via an ACA cron job (the API's in-process
// scheduler is disabled because the app scales to zero).
param deployIngestJob = true
param ingestCron = '30 6 * * *'
