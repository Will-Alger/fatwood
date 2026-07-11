# Operations: ingestion, embeddings, analysis, enrichment

Every write path is an ops concern with two entry points — a CLI verb (not
reachable over HTTP at all) and an `X-Admin-Api-Key`-protected admin endpoint
(returns 404 when no key is configured, so an unconfigured deployment has no
admin surface). Regular users can never trigger ingestion or an LLM call.

## Ingestion

```bash
dotnet run --project src/ResearchDiscovery.Api -- ingest backfill [--days N] [--max-per-category N]
dotnet run --project src/ResearchDiscovery.Api -- ingest delta
```

Exit codes: `0` success, `1` run failed, `2` another run holds the lease,
`64` usage error.

HTTP equivalents (202 + background queue, because a backfill takes tens of
minutes at arXiv's 1-req/3s etiquette):

```bash
curl -X POST -H "X-Admin-Api-Key: $KEY" http://host/api/admin/ingestion/backfill
curl -X POST -H "X-Admin-Api-Key: $KEY" http://host/api/admin/ingestion/delta
curl        -H "X-Admin-Api-Key: $KEY" http://host/api/admin/ingestion/runs
```

Properties worth knowing:
- **Idempotent**: upsert keyed on the unique versionless arXiv ID; re-running
  a backfill adds nothing and duplicates nothing.
- **Per-category high-water mark**: the daily delta fetches only papers newer
  than each category's stored mark; missed days self-heal.
- **Cross-process lease**: a single DB row with a Guid concurrency token
  serializes the in-process scheduler, the CLI, and cloud jobs. A crashed
  holder goes stale after `Ingestion:LockStaleAfterMinutes` (default 120).
- **Scheduled daily job**: `DailyIngestionHostedService` runs a delta at
  `Ingestion:Schedule:TimeUtc` (default 06:30 UTC). In the cloud this is
  disabled in favor of an ACA cron job on the same image.

## Embeddings

Ingestion embeds new papers automatically at the end of every run (the
"needs embedding" query is state-based — anything missed for any reason is
picked up on the next run). Manual backfill, e.g. after changing
`Embeddings:ModelVersion`:

```bash
dotnet run --project src/ResearchDiscovery.Api -- embed
```

Model files (~130 MB) download automatically on first use into
`Embeddings:ModelDirectory`. Progress persists every 512 papers, so
interrupted runs resume. Note: a model swap replaces vectors (keyed by
`ModelVersion`, one row per paper) — search is degraded until the re-embed
completes.

## Profile

Experience and goals live in a single versioned profile (Settings UI, or
`PUT /api/admin/settings/profile`). Analysis is a paper × person judgment,
so every profile edit bumps the version and marks existing analyses stale;
they re-run on the next analysis pass (nothing is deleted).

## Analysis

The API key comes from the environment only — never appsettings:

```bash
export ANTHROPIC_API_KEY=sk-ant-...
```

```bash
dotnet run --project src/ResearchDiscovery.Api -- analyze cs.LG [--max N] [--since-days N]
```

Exit codes: `0` success, `1` one or more papers failed, `64` usage error.

HTTP (category sweep and explicit selection — the latter is what the UI's
"Analyze top N" button calls):

```bash
curl -X POST -H "X-Admin-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"categoryCode":"cs.LG","maxPapers":25,"sinceDays":30}' \
  http://host/api/admin/analysis/run
curl -X POST -H "X-Admin-Api-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"arxivIds":["2506.00764","2506.01509"]}' \
  http://host/api/admin/analysis/selection
curl -H "X-Admin-Api-Key: $KEY" http://host/api/admin/analysis/coverage
```

Cost control is structural: one category or explicit list per run, hard paper
cap, per-paper persistence (a cancelled run keeps paid-for work), and re-runs
skip papers whose analysis is current for the schema + profile version. If
the model declines a paper (possible on security papers), it's recorded and
skipped at zero cost; `Analysis:FallbackModel` opts into a server-side
fallback for deployments where declines matter.

**What gets stored**: one `AnalysisResults` row per paper — the raw
structured JSON (schema v2: feasibility with hard blockers, learning bridge,
effort, reproduce-vs-extend, reference-code likelihood, goal alignment,
resume signal, extension idea, required skills), a denormalized
`CompositeScore` (0–100) for sorting, the producing model, and the profile
version it was judged against.

**Browse integration**: `GET /api/papers` accepts `sort=score_desc` and
`analyzedOnly=true`; each paper carries its analysis when one exists.

## Model selection & cost visibility

Every LLM step (query compiler, paper analysis, relevance judge) has a
UI-selectable model (Settings → Models), validated against the config-driven
registry in `Llm:Models`, which carries per-MTok pricing so action buttons
show live dollar estimates ("Analyze top 25 — est. $0.05"). Bulk steps
default to the cheapest capable tier.

## Signal enrichment

```bash
dotnet run --project src/ResearchDiscovery.Api -- enrich          # citations (Semantic Scholar)
dotnet run --project src/ResearchDiscovery.Api -- enrich --stars  # + GitHub stars (needs GITHUB_TOKEN)
```

Incremental and rate-limit tolerant; signals refresh after 14 days on
re-run. Used as analysis context and future ranking features (measured
harmful as a direct ranking weight on a fresh corpus — see
[search-quality.md](search-quality.md)).
