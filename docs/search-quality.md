# Search Quality: implementation, measurement, and how to keep improving

Audience: a future maintainer (human or Claude session) who needs to improve
ranked search without re-deriving context. Read this before touching anything
in `Search/`, `Eval/`, or `Telemetry/`. The prime directive is at the end;
the short version: **no ranking change ships without a measured delta.**

## 1. Current state (2026-07-12)

**Ranker** (`SearchService.cs`, staged pipeline; every stage a `Ranking`
config flag):

| Stage | What | Status |
|---|---|---|
| 0 | SQL filters (categories, date window, has-code) | always on |
| 1 | Dense retrieval: bge-small-en-v1.5 vectors, 50/50 blend of whole-intent similarity + best-single-topic similarity (`UseMultiAnchor`) | **on** |
| 1a | HyDE: the compiler's hypothetical ideal-paper abstract embedded (no query prefix) as an extra anchor in the best-topic max (`UseHyde`) | **on** |
| 1b | In-memory BM25 (k1=1.2, b=0.75) fused via Reciprocal Rank Fusion, k=60 (`UseHybrid`) | **on** |
| 2 | Signal blend: recency decay, has-code bonus, log-citations (`RecencyWeight` etc.) | all weights 0 (measured: they hurt) |
| 3 | Cross-encoder rerank, top-100 (`UseReranker`) | off (measured: a wash at L-6, a slight loss at L-12) |
| — | Wildcard slots (2 least-experience-similar from the high-relevance pool) | contractual, never remove |

**Score**: nDCG@10 = 0.620 on the 40-query eval set (2026-07-12). MRR 0.956.
Absolute numbers are NOT comparable to the 2026-07-10 campaign below — the
eval set doubled and judgments grew ~50% in between (bigger recall base
deflates scores); only same-day, same-file comparisons are meaningful.

The 2026-07-10 selection campaign (21-query set, identical ground truth):

| config | nDCG@10 | Recall@50 | MRR |
|---|---|---|---|
| single-anchor cosine (original) | 0.523 | 0.530 | 0.897 |
| multi-anchor only | 0.520 | 0.564 | 0.790 |
| hybrid BM25 only | 0.594 | 0.590 | 1.000 |
| multi-anchor + hybrid | **0.614** | **0.606** | **1.000** |
| + cross-encoder rerank (ms-marco L-6) | 0.612 | 0.599 | 0.929 |

The 2026-07-12 campaign (40-query set, all heads judged, identical ground truth):

| config | nDCG@10 | Recall@50 | MRR |
|---|---|---|---|
| multi-anchor + hybrid (prior ship) | 0.600 | 0.645 | 0.930 |
| + cross-encoder rerank (ms-marco L-12) | 0.593 | 0.626 | 0.883 |
| **+ HyDE anchor (shipped)** | **0.620** | **0.673** | **0.956** |

**Ground truth**: `eval/queries.json` (40 queries: 20 original authored + 1
adopted real query + 19 authored 2026-07-12 targeting terminology mismatch,
exact-term/acronym, cross-domain, constraint-heavy, vague-career, and corpus
edge categories) and `eval/judgments.json` (~4,900 LLM-judged (query, paper)
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
7. **MS MARCO rerankers lost a third time** (2026-07-12): L-12 (2× depth of
   L-6) scored 0.593 vs 0.600 control on the 40-query set, MRR down to
   0.883. More capacity made it worse, strengthening the domain-mismatch
   explanation. Don't retry this family. A true domain-appropriate retry
   (bge-reranker-base) is NOT the config-only swap the old backlog claimed:
   it's XLM-RoBERTa — sentencepiece tokenizer, no vocab.txt — so it needs a
   tokenizer path beyond `BertTokenizer` in `OnnxCrossEncoder`. jina-reranker
   v1-turbo-en is BPE (vocab.json + merges) — same problem.
8. **HyDE won and shipped** (2026-07-12): +0.020 nDCG@10 / +0.028 Recall@50
   / +0.026 MRR over control on the 40-query set, all heads judged. 26
   queries improved, 6 flat, 8 worse. Gains concentrate exactly where
   predicted — terminology-mismatch (ai-text-detection +0.09) and
   vague-career (+0.10). The losses concentrate on exact-term queries whose
   topic anchors were already ideal (speculative-decoding −0.21,
   llm-agents-swe −0.17): the abstract can hijack the best-topic max with
   plausible-but-off content. Implementation notes: the abstract embeds
   WITHOUT the query prefix (document-shaped text matching document-side
   vectors); eval plans got abstracts grafted onto frozen anchors so the
   baseline stayed bit-identical (`eval compile` backfills this
   automatically for plans that predate the field).
9. **HyDE blend modes lost twice** (2026-07-12): folding the HyDE vector
   into the whole-intent vector instead of the anchor max. Equal-weight
   blend: 0.617 vs 0.619 anchor — fixes both hijack outliers spectacularly
   (speculative-decoding 0.587→0.772, llm-agents-swe 0.639→0.840, proving
   the hijack mechanism) but pays a small dilution tax on ~20 other queries.
   75/25 primary/hyde: 0.607 — worse than both; partial weight keeps the
   dilution and loses the fix. Anchor mode stays; the two outliers are an
   accepted, documented cost. `Ranking:HydeMode` + `HydeBlendWeight` remain
   for future re-tests (e.g. under a different embedder).
10. **Intent-gated HyDE lost** (2026-07-12, same day): the obvious follow-up
   — compiler classifies query_style (precise/exploratory/mixed), precise
   queries skip the HyDE anchor — scored 0.610 vs 0.620, because HyDE helps
   17 of the 21 precise queries (kv-cache-serving −0.09 under the gate,
   testing-research −0.12) and only 4 wanted it off. "Acronym-dense ⇒ HyDE
   hurts" is FALSE as a class rule; speculative-decoding and llm-agents-swe
   are outliers with an as-yet-unknown cause (likely off-target abstracts —
   inspect those two before inventing any new gate). The plumbing ships
   anyway with `Ranking:UseIntentProfiles=false`: plans now carry `intent`,
   which is useful for eval slicing, telemetry, and as a future LTR feature.

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

~~Grow + diversify the eval set~~ — DONE 2026-07-12 (21 → 40; keep going
via `eval adopt` as real usage accumulates).
~~HyDE~~ — SHIPPED 2026-07-12 (+0.020 nDCG; see lesson 8).
~~Reranker retry~~ — CLOSED for the MS MARCO family (lesson 7); reopen only
with a sentencepiece/BPE tokenizer path for a genuinely different model.

~~Per-intent HyDE gating~~ — MEASURED AND REJECTED same day (lesson 10);
the `intent` field remains on plans for slicing/telemetry/LTR.
~~HyDE blend modes~~ — MEASURED AND REJECTED same day, twice (lesson 9).

1. **Judge calibration**: human spot-audit + sonnet double-judge agreement
   (makes all numbers trustworthy).
   (HyDE outlier autopsy: DONE — abstracts were on-target; cause is the
   anchor-max hijack mechanism, confirmed by the blend experiment in
   lesson 9; no compiler fix available, cost accepted.)
3. **Recency-normalized citations** (citations/day) as a blend feature.
4. **Interleaving in anger**: HyDE-off vs HyDE-on as first live candidate —
   the offline delta is +0.02; a click-vote confirmation would be free.
5. **CI regression gate** on a checked-in mini-corpus.
6. **LTR** once labels cross ~200 (see §4).
7. **Full-text ingestion** (arXiv LaTeX) → section-aware embeddings,
   has-experiments/dataset flags. Big lift, big analysis payoff.

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
