# Running the app

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
| `Llm:Models` | haiku 4.5 / sonnet 5 / opus 4.8 | model registry (allowlist + $/MTok pricing) |
| `Llm:Defaults` | haiku for all steps | default model per step (UI can override per step) |
| `Embeddings:ModelVersion` | `bge-small-en-v1.5` | local embedding model (vectors keyed by version) |
| `Embeddings:QueryPrefix` | bge instruction prefix | prepended to search queries only, never documents |
| `Ranking:UseMultiAnchor` / `UseHybrid` / `UseReranker` | `true` / `true` / `false` | retrieval stages (measured defaults) |
| `Analysis:FallbackModel` | *(empty = no fallback)* | optional server-side fallback on declines |
| `Analysis:DefaultMaxPapers` | 25 | per-run paper cap when unspecified |
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
not `xmin`; embeddings are float32 bytes, not pgvector).

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
  tests; only the running app is restricted to live data), guard the analysis
  JSON contract, pin the offline ranking metrics to hand-computed values, and
  cover the pure retrieval logic (anchor splitting, interleaving, BM25
  tokenization).
- Integration tests host the real API over in-memory Sqlite and cover browse
  filtering/sorting/paging/validation, admin auth (404/401/202), double-run
  upsert idempotency, search ranking with a stub embedder, telemetry logging,
  and the analysis layer (run idempotency, paper caps, decline handling,
  score sorting). The arXiv client and the LLM are stubbed in tests so they
  never leave the process.
