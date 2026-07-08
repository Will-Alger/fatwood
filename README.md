# Research Discovery

A research-to-project discovery tool: browse recent arXiv papers by category to
find portfolio-project candidates. Phase 1 ingests real paper metadata from the
official [arXiv API](https://info.arxiv.org/help/api/index.html) into
PostgreSQL and serves a fast, filterable browse UI. Phase 2 runs an LLM
(Anthropic `claude-fable-5`) over bounded, admin-selected subsets of papers to
score their suitability as solo portfolio projects, and surfaces the stored
analysis in the browse UI (score sort, analyzed-only filter, per-paper detail).

**No mock data anywhere in the product path** — every paper row comes from the
live arXiv API. Tests use saved real responses as fixtures; the running app
always hits arXiv.

## Architecture

```
src/
  ResearchDiscovery.Domain/          Entities only, zero dependencies
  ResearchDiscovery.Application/     Interfaces, options, DTOs (no EF, no HTTP)
  ResearchDiscovery.Infrastructure/  EF Core + Npgsql, arXiv client, ingestion
  ResearchDiscovery.Api/             Web host + controllers + scheduler + CLI mode
tests/
  ResearchDiscovery.UnitTests/       Atom parser tests against a real fixture
  ResearchDiscovery.IntegrationTests/API + upsert tests over in-memory Sqlite
web/                                 React + TypeScript (Vite) browse UI
```

- **Ingestion** is an ops concern with two entry points: a CLI command
  (`ingest backfill|delta`) and an API-key-protected admin endpoint. Regular
  users have no route that can trigger ingestion.
- Ingestion is **idempotent** (upsert keyed on the unique arXiv ID) and tracks
  a **per-category high-water mark** so the daily job only fetches the delta.
- **Browsing** is served entirely from the database and never calls arXiv or
  the LLM.
- A cross-process **database lease** (single row + concurrency token)
  serializes the scheduler and the CLI, which run in separate processes.
- **Analysis** (Phase 2) is an ops concern with the same two-entry-point shape
  as ingestion: a CLI command (`analyze <category>`) and an API-key-protected
  admin endpoint. Regular users have no route that can trigger an LLM call.
  Runs are bounded by construction (one category, capped paper count) and
  idempotent: already-analyzed papers are never re-sent to the model.

## Prerequisites

- .NET 10 SDK
- Node.js 20+ (for the frontend)
- Docker (for PostgreSQL locally, and for the packaged image)

## Run locally (dev loop)

```bash
# 1. Start PostgreSQL
docker compose up -d postgres

# 2. Run the API (applies migrations on startup; listens on :5080)
dotnet run --project src/ResearchDiscovery.Api --launch-profile http

# 3. Run the initial backfill (separate terminal; takes a while at 1 req/3s)
dotnet run --project src/ResearchDiscovery.Api -- ingest backfill

# 4. Run the frontend (proxies /api to :5080; open http://localhost:5173)
cd web && npm install && npm run dev
```

For a faster first backfill while trying things out:

```bash
dotnet run --project src/ResearchDiscovery.Api -- ingest backfill --days 7 --max-per-category 300
```

The high-water-mark design self-heals: if a capped backfill stops early, the
next `ingest delta` continues from where it left off.

## Run the packaged app (single container)

```bash
docker compose up -d --build          # postgres + api (SPA served at :8080)
docker compose run --rm api ingest backfill   # args go to the image entrypoint
docker compose run --rm api analyze cs.LG --max 25   # needs ANTHROPIC_API_KEY in the host shell
# then browse http://localhost:8080
```

## Ingestion

### Manual triggers (admin/ops only)

CLI (preferred for local/initial backfill — not reachable over HTTP at all):

```bash
dotnet run --project src/ResearchDiscovery.Api -- ingest backfill [--days N] [--max-per-category N]
dotnet run --project src/ResearchDiscovery.Api -- ingest delta
```

Exit codes: `0` success, `1` run failed, `2` another run holds the lease,
`64` usage error.

HTTP admin endpoints exist for remote ops, but **only when an admin API key is
configured** (`Admin:ApiKey` / `Admin__ApiKey`). Without a key they return 404
— an unconfigured deployment has no admin surface:

```bash
curl -X POST -H "X-Admin-Api-Key: $KEY" http://host/api/admin/ingestion/backfill   # 202
curl -X POST -H "X-Admin-Api-Key: $KEY" http://host/api/admin/ingestion/delta      # 202
curl        -H "X-Admin-Api-Key: $KEY" http://host/api/admin/ingestion/runs        # status
```

Backfills run asynchronously (a 202 + a queue) because a full backfill takes
tens of minutes at arXiv's rate limit.

### Scheduled daily job

`DailyIngestionHostedService` runs a delta ingestion once a day at
`Ingestion:Schedule:TimeUtc` (default `06:30` UTC). It fetches, per category,
only papers submitted after that category's stored high-water mark. Disable
with `Ingestion__Schedule__Enabled=false`. Overlap with a manual run is
prevented by the database lease — whoever loses logs a warning and skips.

On Azure Container Apps, keep `minReplicas: 1` (scale-to-zero would kill the
in-process scheduler). The production-grade evolution is an ACA cron **Job**
running `ingest delta` on this same image with the scheduler disabled.

## Analysis (Phase 2)

Analysis calls the Anthropic API with **`claude-fable-5`** (structured
outputs enforce the JSON contract server-side) and opts into a **server-side
fallback to `claude-opus-4-8`**: fable-5's safety classifiers can decline
benign security papers (cs.CR is a target category), and the fallback rescues
those inside the same API call instead of losing the analysis. If both
decline, the paper is recorded as declined and skipped.

Set the API key in the environment — it is never read from appsettings:

```bash
export ANTHROPIC_API_KEY=sk-ant-...
```

### Manual triggers (admin/ops only)

CLI (one category per run, newest papers first, capped):

```bash
dotnet run --project src/ResearchDiscovery.Api -- analyze cs.LG [--max N] [--since-days N]
```

Exit codes: `0` success, `1` one or more papers failed, `64` usage error /
unknown category.

HTTP admin endpoints (same `X-Admin-Api-Key` posture as ingestion — 404
without a configured key):

```bash
curl -X POST -H "X-Admin-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"categoryCode":"cs.LG","maxPapers":25,"sinceDays":30}' \
  http://host/api/admin/analysis/run                                    # 202
curl -H "X-Admin-Api-Key: $KEY" http://host/api/admin/analysis/coverage # per-category counts
```

Runs execute on a background queue (202 + poll coverage) because each paper
is one LLM call. Each result is persisted immediately, so a cancelled run
keeps completed work. Cost control is structural: analysis only ever runs
over one category at a time with a hard paper cap, never the whole corpus,
and re-runs skip papers that already have a current-schema analysis.

### What gets stored

One `AnalysisResults` row per paper (1:1, unique FK): the raw structured JSON
(`ResultJson`, schema v1 — feasibility, effort, reproduce-vs-extend guidance,
reference-code likelihood, resume/fintech signal, one concrete extension
idea, required skills), a denormalized `CompositeScore` (0–100) for sorting,
and the model that produced it (the fallback's ID when it served the
request). Bumping `AnalysisOptions.CurrentSchemaVersion` makes stale rows
eligible for re-analysis.

### Browse integration

`GET /api/papers` now accepts `sort=score_desc` (unanalyzed papers last) and
`analyzedOnly=true`, and each paper carries its analysis (score + details)
when one exists. The UI shows a score badge, a "Best project score" sort, an
"Analyzed only" toggle, and an expandable per-paper analysis panel.

## Configuration

Everything is configurable via `appsettings.json` or environment variables
(`__` as the section separator). Key settings:

| Setting | Default | Purpose |
|---|---|---|
| `ConnectionStrings:Default` | local dev Postgres | database |
| `Arxiv:Categories` | `cs.LG, cs.AI, cs.CR, cs.SE, q-fin.CP, q-fin.TR` | target categories |
| `Arxiv:PageSize` | 100 | arXiv page size (`max_results`) |
| `Arxiv:MinRequestIntervalSeconds` | 3 | rate-limit spacing (arXiv etiquette) |
| `Ingestion:Backfill:WindowDays` | 90 | backfill window |
| `Ingestion:Backfill:MaxPapersPerCategory` | 10000 | backfill safety cap |
| `Ingestion:Schedule:Enabled` / `TimeUtc` | `true` / `06:30` | daily delta job |
| `Database:MigrateOnStartup` | `true` | apply migrations at boot |
| `Admin:ApiKey` | *(empty = admin disabled)* | admin endpoint key |
| `Analysis:Model` | `claude-fable-5` | analysis model |
| `Analysis:FallbackModel` | `claude-opus-4-8` | server-side fallback on policy declines |
| `Analysis:Effort` | `medium` | Anthropic effort level (`low`–`max`) |
| `Analysis:DefaultMaxPapers` | 25 | per-run paper cap when unspecified |
| `Analysis:MaxOutputTokens` | 16000 | per-call output ceiling (incl. thinking) |
| `ANTHROPIC_API_KEY` | *(env only)* | Anthropic credential; never in appsettings |

**Reconfigure target categories** with indexed env vars (the ingestion loop is
fully configuration-driven — no category is hardcoded):

```bash
Arxiv__Categories__0=cs.LG
Arxiv__Categories__1=q-fin.ST
```

New categories are picked up on the next run; their first delta falls back to
the backfill window since they have no high-water mark yet.

## Swapping PostgreSQL for SQL Server

Provider-specific code is confined to one registration and the generated
migrations. App code contains no raw SQL and no Postgres-only types (analysis
JSON is `text`, not `jsonb`; the lock uses a portable Guid concurrency token,
not `xmin`).

1. In `ResearchDiscovery.Infrastructure.csproj`, replace
   `Npgsql.EntityFrameworkCore.PostgreSQL` with
   `Microsoft.EntityFrameworkCore.SqlServer` (add the version to
   `Directory.Packages.props`).
2. In `Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`,
   change `options.UseNpgsql(...)` to `options.UseSqlServer(...)` — the single
   provider-specific line.
3. Regenerate migrations (they are inherently provider-specific):
   ```bash
   rm -r src/ResearchDiscovery.Infrastructure/Persistence/Migrations
   dotnet ef migrations add InitialCreate \
     --project src/ResearchDiscovery.Infrastructure \
     --startup-project src/ResearchDiscovery.Api \
     --output-dir Persistence/Migrations
   ```
4. Point `ConnectionStrings__Default` at SQL Server.

## Tests

```bash
dotnet test
```

- Unit tests parse a saved **real** arXiv Atom response (fixtures are fine in
  tests; only the running app is restricted to live data) and guard the
  analysis JSON contract (schema validity, structured-outputs constraints).
- Integration tests host the real API over in-memory Sqlite and cover browse
  filtering/sorting/paging/validation, admin auth (404/401/202), double-run
  upsert idempotency, and the analysis layer (run idempotency, paper caps,
  decline handling, score sorting, analyzed-only filtering). The arXiv client
  and the LLM (`IPaperAnalyzer`) are stubbed in tests so they never leave the
  process.

## Deployment notes (Azure Container Apps)

- Single image (this repo's `Dockerfile`): API + built SPA, no CORS needed.
- Secrets (`ConnectionStrings__Default`, `Admin__ApiKey`) as ACA secrets
  referenced from env vars; nothing sensitive is committed.
- Database: Azure Database for PostgreSQL Flexible Server.
- A standard Azure DevOps pipeline maps directly: `Docker@2` build+push to ACR,
  then `AzureContainerApps@1` deploy. Config is entirely env-driven.
- For production migrations, prefer a pipeline step running an
  [EF migration bundle](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying#bundles)
  and set `Database__MigrateOnStartup=false`; migrate-on-startup is fine for a
  single replica.

## Design decisions & assumptions

Decisions the spec left open, made explicitly:

1. **Query API over OAI-PMH for the backfill.** OAI-PMH sets are whole
   archives (`cs`), not sub-categories, forcing over-fetch and client-side
   filtering; the default 90-day × 6-category window (~30–60k records, ~15–30
   min at 1 req/3s) is comfortably within query-API etiquette. Backfills
   beyond ~a year or whole archives should switch to OAI-PMH.
2. **Delta keyed on `submittedDate`** (the `<published>` timestamp). Revisions
   to already-ingested papers are not re-fetched in Phase 1; switching the
   range to `lastUpdatedDate` with a high-water mark on `<updated>` is a
   drop-in change in `ArxivClient.BuildUrl`/`IngestionService`.
3. **Authors stored as one `"; "`-delimited string** — nothing in Phase 1/2
   queries by author. The API returns them as an array.
4. **Category rows are created during ingestion** from feed terms and the
   configured target list; display names come from a static reference map of
   arXiv's published taxonomy (reference data about arXiv, not seeded product
   data), falling back to the code.
5. **`MaxPapersPerCategory` default 10,000** — sized from live volume
   (~108 papers/day in cs.LG alone) so a 90-day window isn't silently
   truncated.
6. **Browse API is anonymous and read-only**; no user accounts in Phase 1. No
   keyword search (the spec scopes browsing to category filter + date sort).
7. **Unknown category codes in filters are ignored** rather than 400 — stale
   bookmarked URLs degrade gracefully.
8. **One fixed daily run time (UTC).** Missed runs (app down) self-heal:
   the high-water mark persists, so the next delta covers the gap.
9. **Single container serves the SPA**; controllers over minimal APIs; plain
   `fetch` over TanStack Query (two GET endpoints don't justify the
   dependency); no CSS framework.
10. **PKs are `bigint` identity**; the versionless arXiv ID is the natural
    upsert key (unique index), with the version number stored separately.

## Phase 2 design decisions

Made when the analysis layer was built (the Phase 1 seam — `AnalysisResults`
table, `IAnalysisService` interface — slotted in without schema changes):

1. **`claude-fable-5` with a server-side fallback to `claude-opus-4-8`**
   (`server-side-fallback` beta). fable-5's safety classifiers target
   cybersecurity content and can false-positive on benign cs.CR papers; the
   fallback re-serves declined requests in the same call. Papers declined by
   the whole chain are counted and skipped, never failed.
2. **Structured outputs, not prompt-and-parse.** The v1 schema
   (`AnalysisContract.SchemaJson`) is enforced server-side; every field is
   required and numeric ranges live in descriptions (structured outputs
   reject min/max constraints). `ResultJson` is stored verbatim and passed
   through to the UI, so schema evolution is a version bump, not a DTO
   migration.
3. **`IPaperAnalyzer` seam inside the analysis layer** separates the LLM call
   from run orchestration, so tests exercise real selection/persistence/
   idempotency logic with the model stubbed.
4. **Idempotency by schema version, not time**: a paper is re-analyzed only
   when `SchemaVersion` is older than the current contract. Re-running a
   category costs zero tokens for already-analyzed papers.
5. **Per-paper persistence** — each result is saved as it lands; cancelling a
   run keeps completed (paid-for) work.
6. **No analysis lease.** The HTTP queue is single-worker (serialized); a
   concurrent CLI run at worst races on the unique `PaperId` index, which is
   caught and skipped. Duplicate token spend is bounded to one paper.
7. **Effort defaults to `medium`** — abstract-level suitability scoring is
   bounded judgment work; configurable up to `max` via `Analysis:Effort`.
