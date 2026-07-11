# Design decisions & assumptions

Decisions the original spec left open, made explicitly and documented at the
time. Newer, measurement-driven ranking decisions live in
[search-quality.md](search-quality.md).

## Foundation (ingestion & browse)

1. **Query API over OAI-PMH for the backfill.** OAI-PMH sets are whole
   archives (`cs`), not sub-categories, forcing over-fetch and client-side
   filtering; the default 90-day × 6-category window (~30–60k records, ~15–30
   min at 1 req/3s) is comfortably within query-API etiquette. Backfills
   beyond ~a year or whole archives should switch to OAI-PMH.
2. **Delta keyed on `submittedDate`** (the `<published>` timestamp). Revisions
   to already-ingested papers are not re-fetched; switching the range to
   `lastUpdatedDate` with a high-water mark on `<updated>` is a drop-in
   change in `ArxivClient.BuildUrl`/`IngestionService`.
3. **Authors stored as one `"; "`-delimited string** — nothing queries by
   author. The API returns them as an array.
4. **Category rows are created during ingestion** from feed terms and the
   configured target list; display names come from a static reference map of
   arXiv's published taxonomy (reference data about arXiv, not seeded product
   data), falling back to the code.
5. **`MaxPapersPerCategory` default 10,000** — sized from live volume
   (~108 papers/day in cs.LG alone) so a 90-day window isn't silently
   truncated. Capped categories self-heal via the nightly delta.
6. **Browse API is anonymous and read-only**; no user accounts.
7. **Unknown category codes in filters are ignored** rather than 400 — stale
   bookmarked URLs degrade gracefully.
8. **One fixed daily run time (UTC).** Missed runs (app down) self-heal:
   the high-water mark persists, so the next delta covers the gap.
9. **Single container serves the SPA**; controllers over minimal APIs; plain
   `fetch` over TanStack Query (a handful of endpoints don't justify the
   dependency); no CSS framework.
10. **PKs are `bigint` identity**; the versionless arXiv ID is the natural
    upsert key (unique index), with the version number stored separately.

## Personalized discovery & analysis

The governing rule: **the LLM never filters the corpus** — it compiles
search intent (one cheap call per search) and analyzes ranked survivors (one
call per chosen paper), while the sift itself runs on free local embeddings
and BM25. Full original design: [phase-2-redesign.md](phase-2-redesign.md).

1. **A cheap model (`claude-haiku-4-5`), no fallback by default.** Analysis
   is one LLM call per paper — frontier-model pricing multiplies across a
   corpus, so the default is the cheapest current tier. Policy declines
   (possible on security papers) are counted and skipped at zero cost;
   `Analysis:FallbackModel` can opt into the server-side fallback beta —
   with another inexpensive model, not a frontier one.
2. **Structured outputs, not prompt-and-parse.** The analysis schema is
   enforced server-side; every field is required and numeric ranges live in
   descriptions (structured outputs reject min/max constraints on integers).
   `ResultJson` is stored verbatim and passed through to the UI, so schema
   evolution is a version bump, not a DTO migration.
3. **`IPaperAnalyzer` seam inside the analysis layer** separates the LLM call
   from run orchestration, so tests exercise real selection/persistence/
   idempotency logic with the model stubbed.
4. **Idempotency by schema + profile version, not time**: a paper is
   re-analyzed only when its stored analysis predates the current contract or
   profile. Re-running costs zero tokens for current papers.
5. **Per-paper persistence** — each result is saved as it lands; cancelling a
   run keeps completed (paid-for) work.
6. **No analysis lease.** The HTTP queue is single-worker (serialized); a
   concurrent CLI run at worst races on the unique `PaperId` index, which is
   caught and skipped. Duplicate token spend is bounded to one paper.
7. **Search takes plans, not prose.** `POST /api/search` executes a compiled
   plan deterministically; only `POST /api/search/compile` (admin-key gated)
   spends tokens. Chip edits re-execute for free, and identical queries give
   identical results.
8. **Exploration guardrails are structural, not tunable.** Relevance is the
   only ranking signal; experience similarity annotates ("close to home" /
   "stretch") but never gates; two wildcard slots per result set are
   reserved for high-relevance papers least similar to the user's
   experience.
9. **In-memory vector + BM25 indexes, no pgvector.** Keeps the database
   provider-portable and search latency in milliseconds; the trade-off
   (index RAM grows with the corpus) doesn't bite until ~100k+ papers.
