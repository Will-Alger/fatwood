# Operations: ingestion, embeddings, analysis, enrichment

Every write path is an ops concern with two entry points — a CLI verb (not
reachable over HTTP at all) and an admin HTTP route.

**Authorization**: admin routes under `/api/admin/**` require a signed-in
account holding the **`Owner`** role (`[Authorize(Policy = AuthPolicies.Owner)]`);
people-management routes require `Admin`; anything that spends tokens requires
`ActiveUser` (signed in, past the invite gate, with budget). There is **no
admin API-key header** — the single-user `X-Admin-Api-Key` scheme was retired
with the accounts layer, so these routes are driven from the admin UI or with
a bearer token from a signed-in Owner session. See [accounts.md](accounts.md).

## Ingestion

```bash
# Daily delta and short backfills — arXiv query API
dotnet run --project src/ResearchDiscovery.Api -- ingest delta
dotnet run --project src/ResearchDiscovery.Api -- ingest backfill [--days N] [--max-per-category N]

# Deep history — OAI-PMH bulk harvest (this is how the decade corpus was built)
dotnet run --project src/ResearchDiscovery.Api -- ingest bulk --from YYYY-MM-DD [--set cs] [--resume-token T]
```

Exit codes: `0` success, `1` run failed, `2` another run holds the lease,
`64` usage error.

HTTP equivalents (202 + background queue, because a backfill takes tens of
minutes at arXiv's 1-req/3s etiquette) — as an `Owner`:

```
POST /api/admin/ingestion/backfill
POST /api/admin/ingestion/delta
GET  /api/admin/ingestion/runs
```

**`ingest bulk` notes** — OAI sets are whole archives, so sets are derived
from configured category prefixes and filtered client-side (OAI's `from=`
filters on *datestamp*, not submission date). Upserts are **add-only**: OAI
metadata has no version number, so bulk never overwrites rows the query API
owns. Every page logs a resumption token — a stalled harvest resumes with
`--set <set> --resume-token <token>` rather than starting over.

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

Vectors are stored **int8-quantized** (per-vector max-abs scale; exact at 384
dims) alongside the float payload — that is what keeps ~910k papers resident
in a 4 Gi replica. Legacy rows are backfilled by `QuantizeMissingAsync`.

**Index snapshots.** At the end of an embed run, both indexes (int8 vectors,
BM25 postings) are serialized to the `search-index` blob container. A cold API
start downloads them in seconds instead of rebuilding from Postgres; a
database rebuild is the fallback if snapshots are missing. Snapshot codecs
stream through a temp-file spool in both directions, so building or loading
never holds two full copies in memory.

**Search execution tuning** (`Search` config section; results are identical
at any setting — these trade CPU/memory for latency, never quality):

| Setting | Default | Purpose |
|---|---|---|
| `Search:MaxScanParallelism` | 0 (= core count) | Threads for the dense scan; `1` restores the sequential scan |
| `Search:UseCandidateSetCache` | `true` | Serve category/no-code candidate sets from memory; `false` falls back to per-search SQL |
| `Search:CandidateScanDivisor` | 8 | Candidate sets smaller than corpus÷divisor score by id-iteration instead of a full sweep |
| `Search:CandidateCacheTtlMinutes` | 360 | Staleness backstop for the candidate cache (invalidated explicitly on ingest) |
| `Search:Compiler:MinTopics`/`MaxTopics` | 8 / 15 | Anchor-topic range in the compile schema — a *ranking* knob; change only via the eval protocol |
| `Search:Compiler:HydeMinSentences`/`HydeMaxSentences` | 4 / 6 | HyDE abstract length — same rule |

## Profile

Experience and goals live in a per-user versioned profile
(Settings UI, or `GET`/`PUT /api/me/profile` as an `ActiveUser`). Analysis is
a paper × person judgment, so every profile edit bumps that user's version and
marks their existing analyses stale; they re-run on the next analysis pass
(nothing is deleted).

## Analysis

The platform Anthropic key comes from the environment only — never
appsettings (in the cloud it is a Key Vault reference):

```bash
export ANTHROPIC_API_KEY=sk-ant-...
```

Users may also bring their own key (`PUT /api/me/anthropic-key`, stored
encrypted and write-only); their spend then bills to them rather than the
platform budget. See [accounts.md](accounts.md).

```bash
dotnet run --project src/ResearchDiscovery.Api -- analyze cs.LG [--max N] [--since-days N]
```

Exit codes: `0` success, `1` one or more papers failed, `64` usage error.

HTTP — note the two routes have **different audiences**:

```
POST /api/analysis/selection        # ActiveUser — what the UI's "Analyze top N" calls
     {"arxivIds":["2506.00764","2506.01509"]}

POST /api/admin/analysis/run        # Owner — category sweep
     {"categoryCode":"cs.LG","maxPapers":25,"sinceDays":30}
GET  /api/admin/analysis/coverage   # Owner — per-category analyzed/total
```

Work is enqueued to the `analysis-jobs` Storage queue and drained by the
KEDA-scaled `analyze-worker` job; the first few papers of a request run
in-process (hot lane) so results start appearing within seconds instead of
after a cold job start. The worker is also runnable directly:

```bash
dotnet run --project src/ResearchDiscovery.Api -- analyze-worker
```

Cost control is structural: one category or explicit list per run, hard paper
cap, per-paper persistence (a cancelled run keeps paid-for work), and re-runs
skip papers whose analysis is current for the schema + profile version. If
the model declines a paper (possible on security papers), it's recorded and
skipped at zero cost; `Analysis:FallbackModel` opts into a server-side
fallback for deployments where declines matter.

**What gets stored**: one `AnalysisResults` row per (user, paper) — the raw
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

## Search quality (eval harness)

The `eval` verb drives the offline IR harness — compile, judge, score,
calibrate, regrade, export-corpus, seed, and `eval categories [--runs N]` for
category-inference metrics. It is documented in full, with the mandatory
measurement protocol, in [search-quality.md](search-quality.md). **Read that
before changing anything in ranking, eval, or telemetry.**
