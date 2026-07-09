# Deploying ResearchDiscovery to Azure

One Bicep template, one Docker image, two GitHub Actions workflows. Everything
below the "one-time setup" line is automated; nothing in this repo contains a
secret.

```
GitHub push to main
  └─ CD workflow (OIDC login, no stored credentials)
       ├─ az acr build  ──────────────►  Azure Container Registry
       ├─ start migrate job (efbundle) ► one-off Container Apps job → Postgres
       ├─ az containerapp update ─────►  API (Container Apps, scale-to-zero)
       └─ roll ingest-delta cron job image
```

## Expected resources (infra/main.bicep, one resource group)

| Resource | SKU (default) | Purpose | ~$/month |
|---|---|---|---|
| Container Apps env + API app | Consumption, 0.5 vCPU, min 0 replicas | API + SPA, scales to zero | ~$0 idle; pennies per active hour |
| Container Apps jobs ×2 | Consumption | `migrate` (per deploy), `ingest-delta` (daily cron, ~min/day) | < $1 |
| PostgreSQL Flexible Server | Standard_B1ms burstable, 32 GB | database | ~$13 + ~$4 storage |
| Container Registry | Basic | images (+ ACR-side builds) | ~$5 |
| Key Vault | Standard | `db-connection-string`, `admin-api-key`, `anthropic-api-key` | < $1 |
| Log Analytics | PerGB2018, 30-day retention | container logs | < $2 at this volume |

**Total: roughly $20–25/month** at the default minimal footprint. Scaling up
(prod Postgres tier, min 1 replica, Standard ACR) is edits to
`infra/main.bicepparam` only.

## What the user does vs what's automated

| Step | Who | What |
|---|---|---|
| 0. GitHub repo | user | create repo, `git remote add origin …`, push |
| 1. Azure login | user | `az login --use-device-code` (+ `az account set`) |
| 2. Resource group | user | one `az group create` |
| 3. Provision infra | user approves | one `az deployment group create` (idempotent; re-run for infra changes) |
| 4. OIDC identity | user | app registration + federated credential + RG role |
| 5. GitHub secrets/vars | user | 3 secrets (ids, not credentials) + 5 variables |
| 6. Everything after | automated | CI on PRs; CD on every push to main |

## One-time setup

### 1. Log in and pick the subscription

```bash
az login --use-device-code
az account set --subscription "<SUBSCRIPTION_NAME_OR_ID>"
az account show --query '{name:name, id:id}'    # sanity check
```

### 2. Create the resource group

```bash
az group create --name rg-researchdiscovery --location eastus2
```

### 3. Provision the infrastructure

Costs begin here (~$20/mo). Secrets are passed on the command line and land
only in Key Vault. `anthropicApiKey` may be empty — the app runs fine without
LLM analysis until you set it.

```bash
az deployment group create \
  --resource-group rg-researchdiscovery \
  --template-file infra/main.bicep \
  --parameters infra/main.bicepparam \
  --parameters pgAdminPassword='<STRONG_PASSWORD>' \
               adminApiKey='<RANDOM_ADMIN_KEY>' \
               anthropicApiKey='<sk-ant-... or empty>' \
  --query properties.outputs
```

Record the outputs (`acrName`, `containerAppName`, `migrateJobName`,
`ingestJobName`, `apiUrl`) — they become GitHub variables in step 5.

Notes:
- The first deploy runs a public placeholder image (ACR is empty until the
  first CD run); the real app appears after step 6.
- Role assignments in the template require you to have Owner or
  User Access Administrator on the resource group.
- Re-running the deployment **re-asserts the three Key Vault secrets**, so
  pass the same values (or update them deliberately) on infra re-deploys.

### 4. Create the deployer identity (OIDC, no stored credentials)

```bash
# App registration + service principal
APP_ID=$(az ad app create --display-name researchdiscovery-github-cd --query appId -o tsv)
az ad sp create --id "$APP_ID"

# Let it deploy into the resource group
az role assignment create \
  --assignee "$APP_ID" \
  --role Contributor \
  --scope "$(az group show -n rg-researchdiscovery --query id -o tsv)"

# Trust GitHub's OIDC issuer for pushes to main of YOUR repo.
# >>> Replace OWNER/REPO. The subject string must match exactly. <<<
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "github-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:OWNER/REPO:ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

If you later gate CD behind a GitHub *environment* (the workflow declares
`environment: production`), GitHub sends a different subject; add a second
federated credential with `"subject": "repo:OWNER/REPO:environment:production"`.
Having both is harmless.

`workflow_dispatch` runs from other branches will fail OIDC by design — only
`main` (and the environment subject, if added) is trusted.

### 5. Set GitHub secrets and variables

Secrets (identifiers only — with OIDC there is no password or key to leak):

```bash
gh secret set AZURE_CLIENT_ID       --body "$APP_ID"
gh secret set AZURE_TENANT_ID       --body "$(az account show --query tenantId -o tsv)"
gh secret set AZURE_SUBSCRIPTION_ID --body "$(az account show --query id -o tsv)"
```

Variables (from the step 3 outputs):

```bash
gh variable set AZURE_RESOURCE_GROUP --body "rg-researchdiscovery"
gh variable set ACR_NAME             --body "<acrName output>"
gh variable set CONTAINERAPP_NAME    --body "<containerAppName output>"
gh variable set MIGRATE_JOB_NAME     --body "<migrateJobName output>"
gh variable set INGEST_JOB_NAME      --body "<ingestJobName output>"
```

### 6. Push to main

CD builds the image in ACR, runs the migration job, waits for it to succeed,
then rolls the API and the ingest cron job. The step summary prints the app
URL. First data load: run a backfill once via the admin endpoint
(`POST /api/admin/ingestion/backfill` with `X-Admin-Api-Key`), then the daily
`ingest-delta` cron job keeps it current.

## How the pieces work

- **Migrations** run as a one-off Container Apps job executing the EF
  migration bundle (`/app/efbundle`) baked into the image at build time; the
  app itself starts with `Database__MigrateOnStartup=false`. A failed
  migration fails the deploy *before* the API image rolls.
- **Scale to zero** is on by default (`apiMinReplicas 0`). The in-process
  daily scheduler is therefore disabled and a Container Apps **cron job**
  (`ingest-delta`, 06:30 UTC) runs the delta on the same image. Trade-off:
  cold starts re-download the ~90 MB local embedding model; set
  `apiMinReplicas 1` (or mount Azure Files at `Embeddings__ModelDirectory`)
  if that bites.
- **Secrets** live in Key Vault; the app and jobs read them through Container
  Apps secret references using a user-assigned managed identity
  (`Key Vault Secrets User` + `AcrPull`). Nothing sensitive is in source,
  image, or GitHub.
- **Postgres networking**: public endpoint restricted by the "allow Azure
  services" firewall rule (consumption ACA has no stable egress IP). The
  production upgrade is VNet integration + private endpoint — a Bicep change,
  not an app change.
- **Bicep over Terraform**: single-cloud, no state backend to manage, ARM
  what-if for previews. Terraform is only materially better here if this
  needs to join an existing multi-cloud TF estate.

## Teardown

```bash
az group delete --name rg-researchdiscovery
```

(Key Vault soft-delete keeps the vault name reserved ~90 days;
`az keyvault purge --name <kv>` reclaims it immediately.)
