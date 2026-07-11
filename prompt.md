# Build Prompt: Research-to-Project Discovery Tool

You are helping me build a real, working web application. I have already made the
architectural decisions below — your job is to implement them faithfully, flag
anything that is genuinely ambiguous, and push back if a decision is technically
wrong rather than silently working around it.

---

## Product vision

Experienced software engineers who want portfolio/resume projects struggle to
connect recent research to buildable projects. There is no shortage of papers and
no shortage of developers, but the connection is high-friction. This tool closes
that gap: a user picks a domain (e.g. machine learning, security, quantitative
finance), browses recent papers in that domain, and — eventually — sees
pre-computed "how good is this as a solo project" analysis so they can find one
that clicks and go extend it.

The stack and code quality should read as production-grade, not a tutorial —
this project is itself meant to hold up under review by senior engineers.

---

## Hard constraints (do not violate)

1. **No dummy, seeded, mocked, or model-generated data anywhere in the product
   path.** All paper data comes from the live arXiv API. Do not fabricate rows,
   do not stub the API with canned JSON, do not "simulate" ingestion. Tests may
   use fixtures, but the running app must hit real arXiv.
2. **Build in phases.** Phase 1 (ingestion + categorization + browsing) must be
   fully working before any Phase 2 (LLM analysis) code is written. Do not build
   the analysis layer yet — but design the schema and boundaries so it slots in
   without a rewrite.
3. **No LLM calls in Phase 1 at all.** Categorization in Phase 1 is deterministic
   because arXiv already tags every paper with its categories. There is no reason
   to spend a token in Phase 1.

---

## Data source

- Use the official **arXiv API** (`http://export.arxiv.org/api/query`), which
  returns Atom/XML. Consult the current arXiv API docs and terms of use while
  implementing — respect their rate-limit guidance (roughly one request every
  ~3 seconds) and use pagination (`start` / `max_results`) rather than pulling
  everything at once.
- For large initial backfills, evaluate arXiv's **OAI-PMH** bulk-harvest
  interface as an alternative to the query API and use whichever is more
  appropriate; note the choice and why.
- Store per paper at least: arXiv ID, title, authors, abstract, primary category,
  all categories, published date, updated date, PDF/abs URLs, DOI (if present).
  Do **not** download or parse full PDFs in Phase 1 — the abstract is enough.
- Filter by category using arXiv's native taxonomy (e.g. `cat:cs.LG`, `cat:cs.CR`,
  `cat:cs.AI`, `cat:cs.SE`, `cat:q-fin.*`). The set of target categories must be
  **configuration-driven**, not hardcoded in business logic.

---

## Phase 1 — build this now

### Ingestion (this is an ops/service concern, NOT a user-facing feature)

- Ingestion runs as a background service with **two entry points**:
  - a **manual admin trigger** (a CLI command or a protected admin-only endpoint)
    for the initial backfill, and
  - a **scheduled daily job** that pulls only the new delta since the last run.
- **Regular users cannot trigger raw ingestion.** There is no "reseed" button in
  the user-facing app. Clearing and reseeding is an admin/ops command.
- Ingestion is **idempotent**: upsert keyed on arXiv ID. Re-running must never
  create duplicates. Track a per-category high-water mark (last published/updated
  timestamp) so the daily job only fetches new work.
- **Ingest broad:** pull metadata for _all_ configured target categories. This is
  cheap (API + DB rows only). Do not restrict ingestion to a single category —
  that restriction belongs to the (Phase 2) analysis layer, not ingestion.
- Bound the initial backfill: configurable window (default: last 90 days, or last
  N papers per category — make both configurable). Do not attempt to pull all of
  arXiv's history. Respect rate limits with retry/backoff (e.g. Polly).

### Categorization

- Categorization in Phase 1 is purely the arXiv-provided category tags. Persist
  them in a normalized way (a paper has many categories; categories are their own
  table) so browsing and filtering by category is a clean DB query, not string
  matching.

### Browsing (the only user-facing surface in Phase 1)

- A user can: select one or more categories, see the list of recent papers in
  those categories, sort by published date, and read title + abstract + link out
  to arXiv. Paginated. Fast.
- This must run entirely on already-ingested data. Browsing never calls arXiv
  live and never triggers ingestion.

---

## Phase 2 — design the schema to accommodate, but DO NOT build yet

When I later ask for it, the analysis layer will:

- Run an LLM (Anthropic API) over a **filtered subset** of ingested papers —
  typically one category at a time, on demand — to control cost. Never analyze
  the entire corpus indiscriminately.
- Produce **structured JSON** per paper and persist it, with fields such as:
  solo-dev implementation feasibility, estimated effort, reproduce-vs-extend
  guidance, likelihood that reference code already exists, resume/domain signal
  (relevance to the user's stated goals), one concrete extension idea,
  required-skills/stack fit, and a composite score.
- Expose that stored analysis so the browse UI can sort and filter by score.

For Phase 1: leave a clean seam for this (e.g. an `AnalysisResult` table with a
1:1 to papers, and a service interface with no implementation) but write no LLM
code and add no LLM dependency.

---

## Infrastructure & stack

- **Backend:** ASP.NET Core Web API (C#).
- **Data:** EF Core with **PostgreSQL**, code-first migrations. Keep the provider
  swappable — I may switch to SQL Server, so avoid Postgres-only SQL in app code.
- **HTTP:** `HttpClient` with Polly for rate-limit-aware retry/backoff against
  arXiv.
- **Background work:** `BackgroundService`/hosted service for the scheduled job,
  plus a separately-invokable command for the manual backfill.
- **Frontend:** React + TypeScript (Vite). Clean, minimal, fast browse/filter UI
  that calls the API. No form tags-driven state hacks; standard handlers.
- **Packaging/deploy:** Dockerized; target Azure Container Apps, structured so a
  standard Azure DevOps pipeline could build/deploy it. Config (categories,
  backfill window, rate limits, connection strings) via environment/appsettings,
  not hardcoded.
- Secrets/config via environment variables; nothing sensitive committed.

---

## How to work

1. Start by proposing the solution structure (projects, key classes, DB schema,
   config surface) and the exact arXiv query/pagination approach you'll use.
   Wait for my okay before writing the bulk of the code.
2. Then implement Phase 1 end to end: schema + migrations, ingestion service
   (both entry points), the browse API, and the React browse UI.
3. Provide a README covering: how to run locally, how to run the initial backfill,
   how the daily job is scheduled, how to reconfigure target categories, and how
   to swap the DB provider to SQL Server.
4. Call out every place you made an assumption I didn't specify.

## Phase 1 acceptance criteria

- Running the backfill command populates the DB with **real** arXiv papers across
  all configured categories, with no duplicates on re-run.
- The daily job fetches only new papers since the last run.
- Regular users have no way to trigger ingestion.
- The browse UI lets me filter by category and read recent real papers, served
  entirely from the DB, with zero LLM usage.
- Swapping the EF Core provider to SQL Server requires only provider/config
  changes, not rewrites in app logic.
