# Search Quality: implementation, measurement, and how to keep improving

Audience: a future maintainer (human or Claude session) who needs to improve
ranked search without re-deriving context. Read this before touching anything
in `Search/`, `Eval/`, or `Telemetry/`. The prime directive is at the end;
the short version: **no ranking change ships without a measured delta.**

## 1. Current state (2026-07-10)

**Ranker** (`SearchService.cs`, staged pipeline; every stage a `Ranking`
config flag):

| Stage | What | Status |
|---|---|---|
| 0 | SQL filters (categories, date window, has-code) | always on |
| 1 | Dense retrieval: bge-small-en-v1.5 vectors, 50/50 blend of whole-intent similarity + best-single-topic similarity (`UseMultiAnchor`) | **on** |
| 1b | In-memory BM25 (k1=1.2, b=0.75) fused via Reciprocal Rank Fusion, k=60 (`UseHybrid`) | **on** |
| 2 | Signal blend: recency decay, has-code bonus, log-citations (`RecencyWeight` etc.) | all weights 0 (measured: they hurt) |
| 3 | Cross-encoder rerank, ms-marco-MiniLM-L-6-v2, top-100 (`UseReranker`) | off (measured: a wash) |
| — | Wildcard slots (2 least-experience-similar from the high-relevance pool) | contractual, never remove |

**Score**: nDCG@10 ≈ 0.62 on the frozen eval set (was 0.523 for the original
single-anchor cosine ranker on the same ground truth). MRR ≈ 0.94–1.0.

**Ground truth**: `eval/queries.json` (21 queries: 20 authored + 1 adopted
real query) and `eval/judgments.json` (~3,300 LLM-judged (query, paper)
grades 0–3, append-only, rubric v1). Both are versioned repo artifacts —
every change is reviewable in a diff.

**Telemetry** (append-only tables, logged ONLY by API controllers — the eval
CLI must NEVER write telemetry or evaluation poisons its own data):
- `SearchEvents` + `SearchEventResults`: every product search with plan JSON,
  per-rank paper/score/wildcard/proximity, and interleaving `Variant` (A/B).
- `InteractionEvents`: Bookmarked / Unbookmarked / AnalyzedFromSearch /
  NotInterested, joined to `(SearchEventId, Rank)` when the action came from
  search results.

**Signals** (`PaperSignals`, fetched by `enrich` CLI): citation counts
(Semantic Scholar, full corpus), GitHub stars (papers with a repo). Currently
NOT used in ranking (measured harmful — see §3); kept as analysis context
and future learning-to-rank features.

## 2. The measurement system

### Commands (all `dotnet run --project src/ResearchDiscovery.Api -- ...`)

| Command | Tokens | Purpose |
|---|---|---|
| `eval search` | none | Score current ranker: nDCG@10, Recall@50, MRR per query + means. **The regression gate.** |
| `eval judge` | ~$0.1–0.5 | Grade unjudged (query, paper) pairs in the current ranker's pool head + seeded random sample. Incremental, resumable, batched haiku. |
| `eval compile` | pennies | Fill missing plans in queries.json (rare). |
| `eval tune` | none | Grid-search blend weights; prints table, applies nothing. |
| `eval bias` | none | Telemetry skew report + interleaving scoreboard. |
| `eval adopt` | none | Promote logged real queries into queries.json. |
| `eval audit` | none | Missed-gem estimates from random-sample judgments. |

### The comparison protocol (follow it exactly)

1. Implement the candidate behind a `Ranking` flag (or env-overridable config).
2. `eval judge` **with the candidate active** (env vars, e.g.
   `$env:Ranking__UseReranker='true'`) — new rankers surface unjudged papers;
   score deltas are meaningless if the head isn't judged (`judged@10 < 10`).
3. `eval search` for candidate AND control **on the now-identical judgment
   set**. Judgment growth deflates absolute scores (bigger recall base), so
   never compare a new run's number against a remembered old number — rescore
   both.
4. Candidate wins → flip the config default, document the number in README's
   campaign table. Loses → leave the flag, document why (negative results
   prevent re-litigating).

### Known measurement limitations (= improvement backlog for the harness)

- **One LLM judge (haiku), rubric v1.** No error bars. Improve by: (a) human
  spot-audit of ~30 grades to calibrate; (b) double-judge a sample with
  sonnet and report agreement; (c) bump `RubricVersion` on any prompt change
  (mismatch is a hard error by design — never mix rubrics silently).
- **21 queries** — nDCG differences under ~0.02 are noise at this n. Grow
  the set via `eval adopt` (real usage) and judge. 50+ queries would allow
  per-query significance.
- **Pooled recall bias**: Recall@50 is relative to judged papers only. It
  gets more honest as more rankers' heads accumulate. The random samples
  (`source: "random"`) are the unbiased slice — that's what `eval audit`
  leans on.
- **Corpus drift**: scores shift as ingestion grows the corpus. Compare
  rankers on the same DB snapshot, same judgment file, same day.
- **No CI gate yet**: the natural next step is a GitHub Action that restores
  a fixed corpus snapshot + runs `eval search` and fails a PR if nDCG drops.
  Blocker: needs a checked-in mini-corpus (a few hundred papers + vectors)
  since CI can't carry 21k embeddings.

## 3. Measured lessons — do NOT relearn these the hard way

1. **Pure per-topic max-sim tanked nDCG to 0.38** (from 0.55): a paper
   nailing one narrow topic beat papers matching the whole intent. Fix that
   shipped: 50/50 blend of whole-intent and best-topic similarity
   (`InMemoryEmbeddingIndex.TopMultiAsync`). If you touch this, re-measure.
2. **Cross-encoder was a wash (0.612 vs 0.614)** — twice. With the topic
   list as query it *degraded*; with the natural-language interpretation it
   merely tied, at ~2s/search. Plausible causes worth testing before retrying:
   ms-marco is web-search-trained (arXiv abstracts are out of domain); the
   dense+BM25 pool head is already strong. A science-domain cross-encoder or
   a bge-reranker model might change this verdict.
3. **Citation weighting hurt everything** (best citation config 0.52 vs
   0.62): a 90-day corpus is mostly ~0-citation papers, so the weight just
   promotes older papers regardless of fit. Citations may become useful (a)
   as an LTR feature with learned interactions, or (b) recency-normalized
   (citations/day since publication) — untested.
4. **Recency weighting's early win evaporated**: +0.05 recency helped the
   OLD ranker (+0.013), hurts the new stack. Improvements aren't additive;
   re-tune the blend after every retrieval change.
5. **MS MARCO rerankers need natural-language queries** — the plan's
   `interpretation`, never the anchor topic list.
6. **bge-small-en-v1.5 > MiniLM-L6** (0.623 on a harder set vs 0.614 on an
   easier one, consistent with public benchmarks). Query-side prefix is
   REQUIRED (`Embeddings:QueryPrefix`); documents embed without it. A model
   swap re-embeds the whole corpus (~45 min local CPU) and **breaks search
   until re-embed completes** (vectors keyed by `ModelVersion`, one row per
   paper — no side-by-side).

## 4. Using the data as it accumulates

The telemetry turns the user's normal usage into training/eval data. What to
do at each volume milestone:

**Now → ~50 interactions**: run `eval bias` occasionally. Watch: close-to-home%
creeping up (exploration eroding), wildcard yield (0 over many searches =
wildcard selection needs rethinking), position-bias warning (>90% of clicks
in top 3 → labels measure attention, not quality).

**~50–200 interactions**:
- `eval adopt` monthly; judge adopted queries. The eval set converges on the
  real query distribution.
- Start an interleaving experiment for any pending candidate (e.g. the
  cross-encoder): set `Ranking__InterleaveCandidate=true` + a
  `Ranking:Candidate` profile. Clicks become votes; `eval bias` scores it.
  Wait for >20 votes and a clear margin. This is the online check that
  catches what the LLM judge can't see.

**~200+ contextual interactions (the LTR threshold)**:
- Extract labels: Bookmarked/AnalyzedFromSearch = positive, NotInterested =
  hard negative, shown-many-times-never-touched = soft negative. **Discount
  position**: a positive at rank 15 is worth more than at rank 1; the
  standard cheap correction is inverse-propensity by rank (weight ∝ 1/CTR at
  that rank, estimated from the interaction-rank histogram in `eval bias`).
- Train a small gradient-boosted ranker (LightGBM/LambdaMART-style) over
  features already in the DB: dense score, BM25 score, RRF rank, recency,
  has-code, citations, stars, category-match, experience proximity. Serve it
  as a new Stage 2 replacing the linear blend — behind a flag, through the
  protocol.
- Also revisit: fine-tuning the embedder itself on (query text → clicked
  paper abstract) pairs once there are a few hundred.

**Continuous**: every bookmark improves nothing automatically — by design.
The loop is: data accumulates → a human (or Claude session) runs the reports
→ candidate change → offline gate (`eval search`) → optionally online gate
(interleaving) → config flip. Never close this loop automatically; the
self-reinforcing feedback spiral (ranker learns from clicks on what it chose
to show) eats the exploration guarantee first.

## 5. Improvement backlog, in rough expected-value order

1. **Grow + diversify the eval set** via `eval adopt` and authored hard
   queries (cheap, improves every future decision).
2. **Judge calibration**: human spot-audit + sonnet double-judge agreement
   (makes all numbers trustworthy).
3. **Domain-appropriate reranker retry**: bge-reranker-base (ONNX) instead
   of ms-marco; only the model URL/config changes, the stage exists.
4. **HyDE**: compiler writes a hypothetical ideal-paper abstract as an extra
   anchor (abstracts match abstracts better than topic lists do). Requires a
   SearchPlan field + recompiling eval plans, which invalidates head
   coverage → re-judge after. Measure against the 0.62 baseline.
5. **Recency-normalized citations** (citations/day) as a blend feature.
6. **Interleaving in anger**: cross-encoder or HyDE as first candidate.
7. **CI regression gate** on a checked-in mini-corpus.
8. **LTR** once labels cross ~200 (see §4).
9. **Full-text ingestion** (arXiv LaTeX) → section-aware embeddings,
   has-experiments/dataset flags. Big lift, big analysis payoff.
10. **Per-intent ranking profiles** (career-vague vs technical-narrow
    queries have measurably different behavior — see per-query eval output).

## 6. Gotchas that will waste your time if forgotten

- `dotnet run` CWD is the PROJECT dir, not repo root: pass absolute
  `--queries/--judgments` paths; ONNX models download to
  `src/ResearchDiscovery.Api/models/` (gitignored via `models/`).
- Env overrides use double underscore: `$env:Ranking__UseHybrid='true'`.
  Remove them (`Remove-Item Env:...`) before runs meant to use appsettings.
- Structured outputs rejects `minimum`/`maximum` on integers — ranges go in
  the description, clamp in the parser.
- The judge is incremental by (queryId, arxivId): re-running is cheap and
  safe. The random sample is seeded by query id (FNV-1a) so re-runs sample
  identically.
- Sqlite tests share one in-memory connection: background jobs can throw
  "database is locked" into concurrent test queries — retry loops exist in
  TelemetryApiTests; follow that pattern for new tests that race hosted
  services.
- Wildcards are excluded from eval scoring (serendipity, not a ranking
  claim) and MUST stay excluded, or every eval punishes the exploration
  guarantee.
- Cloud (Azure) embedding runs on 0.5 vCPU — a model swap takes multi-hour
  nightly runs to re-embed; batches persist, so it self-heals across nights.
