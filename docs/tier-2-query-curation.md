# Tier 2: Query-Driven Category Curation

*Drafted 2026-07-18. Status: approved direction; Phase B started.*

## The idea

Not all great CS projects come from CS categories — an N-body simulator lives
in astro-ph, an epidemic model in q-bio, a numerical solver in math.NA. So
stop using ingestion-time category selection as the curator. Ingest arXiv's
buildable surface broadly, and let the **query compiler choose categories
per-search** from the user's intent and profile. Categories become a browse
facet and a per-query filter, not a corpus gate.

The architecture already has the socket: the compiler receives
`knownCategories` + the profile and emits `SearchPlan.Categories`, which
gates stage-0. The UI chips let users see and override the choice. Tier 2 is
a role change, not a new subsystem.

## Phases

### A. Corpus substrate (DONE 2026-07-19)
22-category, 10-year corpus fully harvested, embedded (int8), snapshotted.
This is the platform the rest builds on. Done when snapshots are in blob
storage and prod serves the full corpus.
*Landed: 714,811 papers embedded+quantized (0 failures); snapshots
`embeddings-bge-small-en-v1.5.bin` (286 MB) + `lexical-v1.bin` (431 MB) in
blob container `search-index`; snapshot cold-load verified end-to-end
(714,811 vectors / 476,490 terms load in seconds).*

### B. Compiler-as-curator v1 (start now — works on today's corpus)
1. **Taxonomy-aware compiler prompt.** Give the compile step the full known
   category list with one-line descriptions (`ArxivCategoryNames`), and
   explicit instructions: infer *fields* from intent ("pre-med resume
   projects" → q-bio.QM, eess.IV), pick narrowly when the query names a
   field, pick nothing when the query is genuinely cross-domain (empty =
   search everything, a deliberate choice, not a failure).
2. **Category-inference eval.** Extend the eval set with persona-diverse
   queries (pre-med, physics student, audio engineer, backend dev) where
   scoring requires the right category slice. Measure plan.Categories
   precision/recall against judged expectations AND downstream nDCG. Gate:
   compiler changes ship only on eval wins (docs/search-quality.md rules).
   *Harness shipped 2026-07-18: 14 persona queries with
   `expectedCategories`/`acceptableCategories` in eval/queries.json (plan:
   null — invisible to `eval search`/CI until judged) + the `eval categories`
   verb (fresh-compiles, scores P/R/F1, flags taxonomy-unreachable codes).*
   *BASELINE 2026-07-19 (full 715k corpus, haiku compiler): recall 1.00 on
   all 14 queries — every must-have field found (q-bio.QM for pre-med,
   physics.comp-ph for the physics undergrad, econ.EM, eess.SP, ...), zero
   unreachable codes (cross-listings already seed the methods-corner
   taxonomy), and the strict cross-domain control correctly emitted NO
   filter. Mean precision 0.75; every miss is over-inclusion of adjacent
   codes (the mild failure mode — ranking still filters), never a wrong
   field. GATE: PASSED → Phase C proceeds. Downstream-nDCG half rolls into
   the Phase D re-baseline.*
3. **UI transparency.** The interpretation line should say *why* those
   fields were chosen so users trust/correct the chips.

### C. Methods-corner ingestion (STARTED 2026-07-19: categories configured, API+ingest job scaled to 2vCPU/4Gi, harvest round 3 launched)
Add the computational corners of other fields: physics.comp-ph,
physics.data-an, astro-ph.IM, math.OC, math.NA, q-bio.QM, q-bio.PE,
q-bio.BM, eess.IV, eess.AS, eess.SP, eess.SY, stat.ML, stat.CO, econ.EM.
- Code prerequisite: `BulkHarvestService.DeriveSets` must map physics-family
  categories to arXiv's colon-scoped OAI sets (`physics:astro-ph`); verify
  against ListSets before hardcoding.
- Scale prerequisite: ~1M papers → indexes ~1.2–1.3 GB → bump the warm
  replica to 2 vCPU / 4Gi (credit-funded, reversible).
- Cost note: keeping physics.* means paging the whole physics OAI set
  (~an extra hour of polite paging per full harvest).

### D. Ranking at diversity (after C)
- Full eval re-run on the diversified corpus; re-baseline the CI nDCG floor.
- Recency-decay measurement (RankingWeights already models it, flag-off).
- Watch for cross-domain similarity artifacts (bge-small was tuned on
  general text; field-mixed candidate pools may need calibration or a
  larger pool multiplier). Measure before touching.

### E. Buildability signal (last mile)
Category selection still proxies buildability. Promote a direct signal:
- Cheap start: has-code flag + analysis-derived priors (feasibility_score,
  reference_code_likelihood exist per analyzed paper — sparse but real).
- Real version: a small classifier over embeddings ("is this a methods/
  systems paper someone could implement?") trained on a few hundred labels.
- Ships as a flag-off ranking term; eval decides, like everything else.

## Non-goals (for now)
- Whole-arXiv ingestion (hep-th, gr-qc, pure math): low buildability density,
  big index cost. Revisit only after E proves the buildability signal.
- Persona-specific analysis schemas (the med-school question): separate
  product decision; Tier 2 makes the *corpus* serve them, the analysis
  contract is its own workstream.

## Sequencing rationale
B before C: the compiler must be good at category inference on 22 categories
before we hand it 40; each step is eval-gated so regressions can't hide.
