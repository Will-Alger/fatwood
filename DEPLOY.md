# Deploying Fatwood to Azure

One Bicep template, one Docker image, two GitHub Actions workflows. Everything
below the "one-time setup" line is automated; nothing in this repo contains a
secret.

```
GitHub push to main
  └─ CD workflow (OIDC login, no stored credentials)
       ├─ docker build + push ────────►  Azure Container Registry
       ├─ start migrate job (efbundle) ► one-off Container Apps job → Postgres
       ├─ az containerapp update ─────►  API (Container Apps)
       ├─ roll ingest-delta cron job image
       └─ roll analyze-worker job image
```

The image is built **on the runner** (`docker build` + `az acr login`), not
with `az acr build` — ACR Tasks are not permitted on free/MSDN subscriptions.

## Expected resources (infra/main.bicep, one resource group)

| Resource | SKU (template default) | Purpose |
|---|---|---|
| Container Apps env + API app | Consumption, **2 vCPU / 4 Gi**, min 0 replicas | API + SPA. 4 Gi is a functional floor: the in-memory search indexes are ~1.3 GB at the current corpus |
| Container Apps jobs ×3 | Consumption | `migrate` (per deploy), `ingest-delta` (daily cron), `analyze-worker` (KEDA, queue-driven) |
| PostgreSQL Flexible Server | Standard_B1ms burstable, 32 GB | database |
| Container Registry | Basic | images |
| Storage account | Standard | `analysis-jobs` queue + `search-index` blob container (index snapshots, ~900 MB) |
| Key Vault | Standard | `db-connection-string`, `anthropic-api-key`, `acs-connection-string`, `storage-connection-string` |
| Log Analytics | PerGB2018, 30-day retention | container logs |
| Azure Communication Services | *(optional, provisioned separately)* | branded OTP email; empty connection string = Microsoft default emails |

**Cost — two honest numbers:**

- **Template defaults** (scale-to-zero, B1ms): roughly **$25–30/month** — ACR
  ~$5, Postgres ~$17, storage/Key Vault/logs a few dollars, API ~$0 while idle.
- **As production actually runs today**: roughly **$70–95/month.** Two
  deliberate departures from the defaults were applied *imperatively* and are
  documented in "Drift you should know about" below.

## What you do vs what's automated

| Step | Who | What |
|---|---|---|
| 0. GitHub repo | you | create repo, `git remote add origin …`, push |
| 1. Azure login | you | `az login --use-device-code` (+ `az account set`) |
| 2. Resource group | you | one `az group create` |
| 3. Provision infra | you approve | one `az deployment group create` (idempotent; re-run for infra changes) |
| 4. OIDC identity | you | app registration + federated credential + RG role |
| 5. GitHub secrets/vars | you | 3 secrets (ids, not credentials) + 6 variables |
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
az group create --name rg-researchdiscovery --location centralus
```

> **Region gotcha.** The template's `location` default is `eastus2`, but this
> subscription is **offer-restricted from provisioning PostgreSQL Flexible
> Server there** (`LocationIsOfferRestricted`), and so is `westus2`.
> Production runs in **centralus**. Verify before you commit to a region:
> `az rest --method GET --url "https://management.azure.com/subscriptions/<SUB>/providers/Microsoft.DBforPostgreSQL/locations/<REGION>/capabilities?api-version=2024-08-01"`

### 3. Provision the infrastructure

Costs begin here. Secrets are passed on the command line and land only in Key
Vault. `anthropicApiKey` may be empty — the app runs fine without LLM features
until you set it.

```bash
az deployment group create \
  --resource-group rg-researchdiscovery \
  --template-file infra/main.bicep \
  --parameters location=centralus \
               pgAdminPassword='<STRONG_PASSWORD>' \
               anthropicApiKey='<sk-ant-... or empty>' \
               userAuthAuthority='https://<tenant>.ciamlogin.com/<tenantId>/v2.0' \
               userAuthAudience='<api app registration client id>' \
               bootstrapAdminEmail='<you@example.com>' \
               acsConnectionString='<ACS connection string or empty>' \
  --query properties.outputs
```

Record the outputs (`acrName`, `containerAppName`, `migrateJobName`,
`ingestJobName`, `analyzeJobName`, `apiUrl`) — they become GitHub variables in
step 5.

Notes:
- **There is no `adminApiKey` parameter.** The single-user admin API key was
  retired with the accounts layer; admin routes are now authorized by role
  (`Owner`) on a real signed-in account. `bootstrapAdminEmail` is what
  promotes the first account to Admin on its first sign-in.
- Leaving `userAuthAuthority`/`userAuthAudience` empty turns user auth **off**
  — appropriate only for a private test deployment. See
  [docs/accounts.md](docs/accounts.md) for the account platform.
- The first deploy runs a public placeholder image (ACR is empty until the
  first CD run); the real app appears after step 6. The placeholder listens on
  8080 to match the ingress target port — a port-80 image never passes
  readiness.
- Role assignments in the template require Owner or User Access Administrator
  on the resource group.
- Re-running the deployment **re-asserts the Key Vault secrets**, so pass the
  same values (or update them deliberately) on infra re-deploys.

### 4. Create the deployer identity (OIDC, no stored credentials)

```bash
# App registration + service principal
APP_ID=$(az ad app create --display-name fatwood-github-cd --query appId -o tsv)
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

The CD workflow declares `environment: production`, and GitHub sends a
**different subject** for environment-gated jobs. Add the second credential
too — having both is harmless and avoids a confusing OIDC failure:

```bash
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "github-env-production",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:OWNER/REPO:environment:production",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

`workflow_dispatch` runs from other branches will fail OIDC by design — only
`main` and the production environment are trusted.

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
gh variable set ANALYZE_JOB_NAME     --body "<analyzeJobName output>"
```

### 6. Push to main, then load data

CD builds the image, runs the migration job, waits for it to succeed, then
rolls the API and both worker jobs. The step summary prints the app URL.

First data load — run the bulk harvest as a one-off job (or from a desktop
with `ConnectionStrings__Default` pointed at the cloud database):

```bash
# History via OAI-PMH; then the daily cron delta keeps it current.
dotnet run --project src/ResearchDiscovery.Api -- ingest bulk --from 2016-07-17
```

Ops HTTP routes (`/api/admin/**`) require signing in as an account with the
**Owner** role — there is no API-key header. See
[docs/operations.md](docs/operations.md).

## How the pieces work

- **Migrations** run as a one-off Container Apps job executing the EF
  migration bundle (`/app/efbundle`) baked into the image at build time; the
  app starts with `Database__MigrateOnStartup=false`. A failed migration
  fails the deploy *before* the API image rolls.
- **Search index snapshots.** Both indexes (int8 vectors, BM25 postings)
  serialize to the `search-index` blob container. A cold API start downloads
  prebuilt snapshots in seconds instead of rebuilding from Postgres; a
  database rebuild is the fallback. Snapshots are written by the embed CLI and
  the daily ingest job.
- **Analysis queue.** Admin/user-triggered analysis enqueues to the
  `analysis-jobs` Storage queue; the `analyze-worker` job is KEDA-scaled on
  queue depth. The first few papers run in-process (hot lane) so results start
  appearing immediately.
- **Scale to zero** is the template default (`apiMinReplicas 0`), so the
  in-process daily scheduler is disabled and a Container Apps **cron job**
  (`ingest-delta`, 06:30 UTC) runs the delta on the same image. Trade-off:
  a cold start re-downloads the ~130 MB embedding model (it is not baked into
  the image) and reloads index snapshots. Set `apiMinReplicas 1` to avoid it.
- **Secrets** live in Key Vault; the app and jobs read them through Container
  Apps secret references using a user-assigned managed identity
  (`Key Vault Secrets User`, `AcrPull`, plus Storage Queue/Blob Data
  Contributor). Nothing sensitive is in source, image, or GitHub.
- **Postgres networking**: public endpoint restricted by the "allow Azure
  services" firewall rule (consumption ACA has no stable egress IP). Desktop
  bulk runs need a temporary named firewall rule — **remove it afterwards**.
  The production upgrade is VNet integration + private endpoint.
- **No Easy Auth.** A platform-level (`authConfigs`) login wall fronted the
  whole site during the single-user era and was **retired 2026-07-12**; the
  app now does its own Entra External ID JWT auth with anonymous browsing by
  design. The template deliberately omits it so an infra deploy can never
  re-erect a login wall over the public site.
- **Bicep over Terraform**: single-cloud, no state backend to manage, ARM
  what-if for previews. Terraform would only win here if this had to join an
  existing multi-cloud estate.

## Drift you should know about

Two production settings were applied **imperatively** and are intentionally
*not* the template defaults, so that an infra re-deploy reverts to the cheap
configuration when the free-credit era ends:

| Setting | Template default | Production today | Undo |
|---|---|---|---|
| Postgres SKU | `Standard_B1ms` | `Standard_B2ms` (~$50/mo) — B1ms burst credits were exhausted by the embed backfill | `az postgres flexible-server update -g rg-researchdiscovery -n <server> --sku-name Standard_B1ms --tier Burstable --yes` |
| API min replicas | `0` (scale-to-zero) | `1` (always warm, indexes stay in RAM) | `az containerapp update -g rg-researchdiscovery -n rdisc-api --min-replicas 0` |

Re-running `az deployment group create` **will** reset these to the template
defaults. That is deliberate; re-apply them afterwards if you still want them.

## Teardown

```bash
az group delete --name rg-researchdiscovery
```

(Key Vault soft-delete keeps the vault name reserved ~90 days;
`az keyvault purge --name <kv>` reclaims it immediately.)
