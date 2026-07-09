# Phase 2 Redesign: Personalized Discovery

Supersedes the fixed-persona analysis built on `phase-2-llm-analysis`. Core
shift: analysis value is a property of **paper × person**, not paper. The LLM
never filters the corpus; it compiles intent and analyzes survivors.

## The funnel

```
15k papers
  │  Stage 0: deterministic filters (categories, date) — free, existing
  ▼
 ~8k
  │  Stage 1: embedding rank — query-driven anchor, local model, free
  ▼
 ~150
  │  Stage 2: enrichment — Papers With Code lookup (code exists?), free
  ▼
 ~150 ranked
  │  Stage 3: personalized LLM analysis — pennies, on demand
  ▼
 browse: Matches view
```

## Components

### 1. Profile (single user)
Skills, domains, target role/city, weekly time budget, stretch appetite.
One DB row + small UI panel. Accretes from queries: when a search contains a
durable fact ("3 years fullstack"), offer one-click "save to profile?" —
never silently persist. Precedence: query overrides profile per-search;
profile fills gaps.

### 2. Smart search (query compiler) — LLM call site #1
One cheap call per search. Natural language → structured SearchPlan:
`{ interpretation, anchorText, categories[], dateWindow, effortCeiling,
codeExists?, experienceContext }`. Key job is career intent → research topic
expansion ("fintech NYC" → market microstructure, fraud detection, payments
infra, risk modeling...). The plan renders as **editable chips** above
results — correct the compiler by clicking, not re-prompting. The
interpretation line is always shown (trust/transparency).

### 3. Embedding rank — no LLM, no tokens
Local embedding model (small ONNX sentence transformer) embeds every abstract
once at ingestion. Vectors stored as plain float arrays (NOT pgvector —
provider portability); cosine over ~15k rows computed in-process, instant.
Rank by similarity to the SearchPlan anchorText.

**Exploration guardrails (hard requirements):**
- Experience similarity NEVER gates or ranks. It only annotates ("close to
  home" / "stretch" badge) plus an optional low-default slider.
- Language/framework/tooling differences are explicitly not feasibility
  signals anywhere in the pipeline.
- Wildcard slots: every results page reserves ~2 slots for high
  goal-relevance papers sampled from OUTSIDE the experience cluster
  (diversity sampling). Serendipity is structural, not opt-in.

### 4. Enrichment — Papers With Code
Free API, keyed by arXiv ID: does public code exist? Better than LLM
guessing. Enables the contribution angle: relevant + no code + feasible =
reproduction gap. Stored per paper; badge in UI; `codeExists` filterable.

### 5. Personalized analysis — LLM call site #2
Reuses the branch's plumbing (AnalysisResults table, queue, CLI/admin
triggers, structured outputs). Prompt takes the profile. Rules:
- Learnable ≠ blocker: flag only hard blockers (specialized hardware,
  proprietary datasets, months of prerequisite theory).
- Output a **learning bridge** ("~1 week of PyTorch; your pipeline
  experience covers the rest"), not a flat feasibility number.
- Cache key: (paper, profile-version). Query drives selection, profile
  drives analysis — results reusable across searches; re-analyze only on
  profile edits (bump profile version).
Triggered from UI: "Analyze top N — est. $X". Optional call site #3: a
finalist deep-dive pass over the top ~20 with a stronger model.

### 6. Model configuration (UI)
- **Model registry** in config: allowlisted models + input/output $/Mtok.
- **Per-step assignment** stored in DB, editable in a UI settings panel:
  QueryCompiler / PaperAnalysis / FinalistDeepDive dropdowns. Defaults:
  haiku for all bulk steps (cost rule: bulk analysis stays on cheap tier).
- **Live cost estimates** on every action button, computed from registry
  pricing × token estimates; confirmation step when a selection makes a bulk
  action expensive.
- Provenance: stored analyses record the producing model (already do);
  shown in UI.

## Token economics (the point)
- Query compile: ~1 call/search, fraction of a cent.
- Corpus sift: zero tokens (local embeddings).
- Analysis: top ~150 papers × haiku ≈ $0.15–0.50; finalist pass ~20 papers
  with a stronger model < $1. Weekly re-runs: pennies.
- Anti-goal: never run LLM analysis over the whole corpus.

## What survives from `phase-2-llm-analysis`
Analysis table + schema versioning, job queue + hosted service, CLI/admin
triggers, structured-output client, decline handling, UI analysis panel.
What changes: prompt becomes profile-aware (schema v2), analysis becomes
on-demand over ranked slices instead of per-category sweeps, plus the new
profile/search/embedding/enrichment/model-settings components.
