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

// API: scale to zero when idle. Production: apiMinReplicas 1+ (also avoids
// re-downloading the ~90 MB local embedding model on cold starts).
param apiMinReplicas = 0
param apiMaxReplicas = 2
param apiCpu = '0.5'
param apiMemory = '1Gi'

param acrSku = 'Basic'
param logRetentionDays = 30

// Daily arXiv delta at 06:30 UTC via an ACA cron job (the API's in-process
// scheduler is disabled because the app scales to zero).
param deployIngestJob = true
param ingestCron = '30 6 * * *'
