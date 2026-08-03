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
   *PARSIMONY NUDGE MEASURED AND REJECTED 2026-07-19: a "smallest
   sufficient set / don't pad adjacent fields" prompt rewrite gained 0.01
   precision but broke recall on quant-trading (missed BOTH must-have
   q-fin codes) and flipped the cross-domain control from correctly-empty
   to a guessed cs-list; mean F1 0.83 → 0.74. Reverted. Lesson: the
   current prompt's over-inclusion is cheap (ranking filters); pushing
   haiku toward parsimony trades it for under-inclusion, which is not.
   Single-run haiku variance is real (±0.1 per-query precision) — any
   future prompt comparison should average 3 runs
   (`eval categories --runs 3` does this in one command since 2026-07-22).*
   *METHODS-CORNER EXPANSION 2026-07-23: +8 persona queries (astronomy
   pipelines, logistics OR, CFD/PDE, epidemiology, computational neuro,
   MPC control, Bayesian computation, climate ML) whose must-have codes —
   astro-ph.IM, math.OC, math.NA, q-bio.PE, q-bio.NC, eess.SY, stat.CO,
   physics.ao-ph — were never before must-haves in the eval (only
   acceptable or absent), i.e. the 2026-07-19 recall-1.00 baseline never
   tested routing INTO the Phase C corners. Shipped with plan: null
   (invisible to `eval search`/CI until compiled+judged). Not yet
   baselined: run `eval categories` (ideally `--runs 3` once the
   multi-run averaging lands) to measure, then compile+judge to fold
   their downstream-nDCG half into the next re-baseline.*
   *WITHIN-CS EXPANSION 2026-07-29: the 22 category-eval queries tested one
   axis — routing OUT of CS into the methods corners — and only six CS codes
   were ever must-haves (cs.DB, cs.DC, cs.GR, cs.HC, cs.RO, cs.SD). Nothing
   tested the opposite failure: the compiler defaulting to the corpus giants
   (cs.LG 269k, cs.CV 193k, cs.AI 182k, cs.CL 110k) when the right slice is a
   narrow CS subfield. +8 persona queries whose must-haves — cs.CR (45k live
   papers), cs.SE (25k), cs.IR (23k), cs.NI (20k), quant-ph (10k), cs.SI+cs.CY
   (10k+18k), cs.PL (7.7k), cs.AR (4.8k) — were never expected NOR acceptable
   anywhere in the eval, i.e. nine of the largest live categories had no
   ground truth at all. Their acceptable sets deliberately omit cs.LG/cs.AI
   where those are not defensible for the intent (a CTF persona, a compilers
   persona), so reflex ML picks score as precision errors — that is the whole
   point of the slice, and it means this batch's precision is NOT comparable
   with the permissive methods-corner batch. `policy-data-newsroom` is also a
   deliberate contrast with the `data-journalist-broad` control: same
   candidate codes (cs.SI/cs.CY/stat.AP/physics.soc-ph), but the specific
   query should filter where the vague one should not. Shipped with
   plan: null (invisible to `eval search`/CI until compiled+judged), so the
   un-baselined backlog is now 16 queries — run `eval categories --runs 3`
   over the 30 targets to baseline both batches in one pass. Codes verified
   against the live `/api/categories` list and pinned by the new
   `EvalQueryFixtureTests` (unknown code, duplicate id, expected∩acceptable
   overlap, and plan-null-without-expectations now fail at PR time instead of
   being silently reported as a corpus gap). Still no must-have anywhere:
   cs.MA, cs.LO, cs.GT, math.PR, stat.ME, cond-mat.mtrl-sci, physics.optics.*
   *THEORY/PHYSICAL-SCIENCE EXPANSION 2026-07-30: +6 persona queries closing
   exactly the seven-code list the previous note left open. Five of them —
   cs.MA (9,454 live papers), math.PR (7,759), cs.GT (6,032),
   cond-mat.mtrl-sci (6,123), physics.optics (3,963) — had NO ground truth
   anywhere in the eval, expected or acceptable; the other two, stat.ME
   (11,800) and cs.LO (5,692), were acceptable-only on three queries each and
   so had never been a must-have. Must-have codes 32 → 39. The batch mixes
   its ML posture deliberately, and per query rather than per batch:
   `materials-simulation-switcher` keeps cs.LG acceptable (ML-for-materials is
   a real cross-listed literature), while `applied-stats-methods` and
   `verification-curious-dev` omit cs.LG/cs.AI/stat.ML entirely, so a reflex
   ML pick scores as a precision error — the same convention as the within-CS
   batch, and the same caveat: precision is not comparable across batches with
   different permissiveness. `mechanism-design-simulation` is the one
   two-code must-have (cs.GT + cs.MA) and tests whether strategic-behaviour
   intent routes to game theory rather than to multi-agent RL. Shipped with
   plan: null, so `eval search` and the CI gate still skip them
   (`EvalRunner.ScoreAsync` filters `Plan is null`) and the nDCG floor is
   untouched; the un-baselined backlog is now 22 queries across three batches
   — one `eval categories --runs 3` over the 36 targets baselines all of them.
   NEXT GAP, and it is the mirror of this one: cs.LG (269k), cs.CV (193k),
   cs.AI (182k), cs.CL (110k), stat.ML (72k), cs.SY (41k) and cs.NA (38k) are
   acceptable-only — nothing in the eval asserts that a corpus giant IS the
   right slice, so over-routing away from ML would not be caught. 66 live
   codes still have no ground truth at all, led by math.IT (17k).*
   *CORPUS-GIANT EXPANSION 2026-08-01: +5 persona queries making cs.CV, cs.CL,
   cs.LG, cs.AI and stat.ML must-haves for the first time — the mirror test the
   previous note called for. Every earlier batch scored a compiler that routes
   AWAY from the giants; nothing scored one that routes away when the giant is
   correct, and each of these five is a query where the big category genuinely
   IS the field (an on-device vision feature, an archive-text project, a
   from-scratch training-method reimplementation, classical planning/search, and
   uncertainty quantification). Must-have codes 39 → 44; the eval is 76 → 81
   queries, 22 → 27 of them plan: null.
   Two corrections to the list above, both verified this run. (1) cs.SY and
   cs.NA are NOT gaps of the same kind: arXiv aliases them onto eess.SY and
   math.NA (`ArxivCategoryNames` gives each pair one display name — "Systems
   and Control", "Numerical Analysis"), and the eval already handles them the
   right way — `drone-mpc-control` expects eess.SY with cs.SY acceptable,
   `cfd-solver-portfolio` expects math.NA with cs.NA acceptable. Their
   acceptable-only status is deliberate alias-forgiveness, so authoring
   must-haves for them would demand a specific spelling of a category the
   compiler may legitimately name either way. They are excluded here on
   purpose. (2) This batch is DELIBERATELY PERMISSIVE — the opposite convention
   from the within-CS and theory batches: cs.LG is acceptable on all four
   queries where it is not itself the must-have, and cs.AI on three of its
   four, because when the giant is the right field its neighbours usually are
   too. Its precision is therefore not comparable with those
   batches, only its recall is the point. stat.ML in particular is scored with
   cs.LG acceptable rather than expected: the two are heavily cross-listed but
   are distinct codes, and `uncertainty-quant-statistician` asks for the
   statistical machinery, so cs.LG-only should cost recall while cs.LG-as-extra
   should not. Shipped with plan: null, so `eval search` and the CI nDCG floor
   are untouched; the un-baselined backlog is now 27 queries across four
   batches — one `eval categories --runs 3` over the 41 targets baselines all
   of them. 67 of the 155 live codes still have no ground truth at all, led by
   math.IT (17k), astro-ph.HE (4.2k) and math.CO (3.5k).
   NOT DONE, and it is what would make the alias mistake impossible rather than
   merely documented: `EvalQueryFixtureTests` has no assertion that an
   alias pair is never split across must-have and absent — a future batch can
   still expect cs.SY without forgiving eess.SY, and nothing would fail. That
   test needs a taxonomy-level alias set (codes sharing a display name), which
   is a C# change and could not be built in the steward environment.*
3. **UI transparency.** The interpretation line should say *why* those
   fields were chosen so users trust/correct the chips.
   *Taxonomy naming caught up with the corpus 2026-07-23:
   `ArxivCategoryNames` grew 51 → 155 entries so every live corpus code
   (cross-listings included) gets a real name in the compiler's known-category
   list and the UI; `/api/categories` now re-resolves rows whose stored name
   is still the bare code (rows are named once at creation and were never
   refreshed).*
   *Client glosses caught up 2026-07-27: `web/src/data/categoryGloss.ts` grew
   66 → 147 exact codes so all 155 live corpus categories now render a
   plain-English "what that actually means" line instead of an archive-level
   placeholder ("Physics.", "Mathematics."). The bland lines were both a
   transparency gap on the search-plan chips and a search gap — CategoryFilter
   matches on gloss text, so e.g. cs.SY (41k papers), cs.NA (38k), math.IT
   (17k), q-bio.PE, physics.flu-dyn were unfindable by what they are about.
   Fallbacks stay for codes that arrive later via cross-listing.*
   *Chips became self-explanatory 2026-07-28: the plan chips rendered a bare
   arXiv code (`q-bio.QM`) with the explanation only in a `title` tooltip —
   unreachable on touch, which is where the non-expert personas B.2 targets
   actually are. Chips now carry the field's real name from the live
   `/api/categories` list (passed down from `App`, not re-fetched), and
   dropping a chip is reversible: `Discover` remembers the category set the
   planner chose at compile/replay time and offers "Restore the original
   fields" once the user has edited it. Correction was one-way before —
   the only way back was spending another compile. Together with the gloss
   pass above: the chip now names the field, the tooltip explains it.*
   *The tooltip half finished on touch 2026-08-02: the 2026-07-28 pass fixed
   the code (`q-bio.QM` → "Quantitative Methods") but left the explanation
   itself hover-only — `categoryGloss` reached the plan chips through a
   `title` attribute and nowhere else, while `CategoryFilter` had rendered the
   same gloss as visible text since the 2026-07-27 pass. The chip's name was
   also being truncated: `.chip-category-name` caps at `max-width: 22ch`, and
   46 of the 155 live `/api/categories` names are longer than 22 characters
   (`astro-ph.IM` = "Instrumentation and Methods for Astrophysics", 44), so
   for those the chip showed an ellipsis and the full name appeared in no
   tooltip at all — the chip's `title` holds the gloss, not the name. A
   "What these fields mean" `<details>` block under the chips now spells out
   code, full name and gloss for each planned category, so both are reachable
   by touch and keyboard; the caption dropped its "hover for detail"
   instruction, which named an affordance half the audience does not have.
   The tooltips stay for pointer users. NOT DONE: nothing pins the coverage
   invariant this rests on — `categoryGloss` has 147 exact codes and the
   other 8 live categories (`gr-qc`, `hep-th`, `hep-ph`, `hep-ex`, `hep-lat`,
   `math-ph`, `nucl-th`, `nucl-ex`) resolve through the archive fallback,
   which happens to read correctly only because they are dotless archive-level
   codes. A new dotted code arriving via cross-listing would silently render
   "Physics." in this block, and no build or test would notice. The frontend
   CI gate is `npm run build` (there is no web test runner in the repo), so
   the enforceable version is a type-level one: pin the live code list and
   type `byCode` as a total `Record` over it.*
   *COVERAGE PINNED 2026-08-03: that is now enforced, so the NOT DONE above is
   closed. `web/src/data/liveCategoryCodes.ts` holds `LiveCategoryCode`, a
   type-only union of the 155 codes `/api/categories` serves (types only — no
   value is imported, so it is erased at compile time and adds no bundle
   bytes), and `categoryGloss.ts` checks both tables against it: every dotted
   live code must have its own `byCode` line — its archive line is the
   degradation being guarded against, so archive coverage does not excuse it —
   while a dotless archive-level code (`hep-th`) is answered exactly by
   `byArchive`, so either table satisfies it. A second assertion bans a gloss
   keyed to a code the corpus does not serve, which is what catches a typo:
   `cs.RO` mistyped as `cs.R0` otherwise reads as one missing gloss and one
   mystery entry, neither of them an error. Failures name the code
   (`Type '"q-bio.QM"' does not satisfy the constraint 'never'`) and were
   confirmed by fault injection, not assumed — an earlier draft of the check
   passed a deleted `q-bio.QM` gloss because `q-bio` covered it. Live shape
   today: 155 codes, 146 dotted with exact lines, plus `quant-ph`, which is
   dotless but has its own line; the 8 resolving through `byArchive` are
   exactly the dotless codes the note above lists. LIMIT, and it is the reason
   this is a pin and not a check: the union is a snapshot, so a category that
   appears tomorrow still renders its archive line until someone regenerates
   the list (command in the file header). What is gone is the silent version —
   once the list is refreshed, a missing gloss fails `tsc` instead of being
   noticed by nobody. Removing the human step entirely would take a CI job
   diffing the pin against live `/api/categories`; not attempted here, since it
   puts a network dependency in the build gate. STILL OPEN, from B.2's
   2026-08-01 note: `EvalQueryFixtureTests` has no assertion that an alias pair
   (`cs.SY`/`eess.SY`, `cs.NA`/`math.NA`) is never split across must-have and
   absent. That one is C#, and no .NET SDK was available in the steward
   environment.*

### C. Methods-corner ingestion (DONE 2026-07-19)
*Landed: round-3 harvest of econ/q-bio/eess/stat/physics:astro-ph/
physics:physics/math added 195,922 papers → corpus 910,733 / 37
categories; snapshots 364MB+537MB (551,388 terms) live; prod serving the
full corpus at 2vCPU/4Gi. The 14 persona queries are compiled+judged and
now score in `eval search` (their downstream-nDCG half is live).*
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
