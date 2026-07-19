import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  analyzeSelection,
  compileSearch,
  getPapersByIds,
  getRecentSearches,
  replaySearch,
  runSearch,
} from '../api/client'
import type {
  LlmSettingsView,
  MeView,
  PaperDto,
  RecentSearchSummary,
  SearchPlan,
  SearchResult,
} from '../api/types'
import { useAnalyze, pollUntilAnalyzed } from '../hooks/useAnalyze'
import { useTypingPlaceholder } from '../hooks/useTypingPlaceholder'
import { PaperCard } from './PaperCard'
import { RecentSearches } from './RecentSearches'
import { EmberDots, PaperSkeletons } from './Skeletons'
import { categoryGloss } from '../data/categoryGloss'

const SEARCH_STAGES = [
  'Sifting tens of thousands of papers…',
  'Matching your exact terms…',
  'Scoring the survivors by meaning…',
  'Picking two wildcards from outside your lane…',
]

const RESULT_LIMIT = 30
const ANALYZE_OPTIONS = [5, 10, 15, 20, 25]
const ANALYZE_DEFAULT = 10
// Reveal completed analyses strictly in rank order with a beat between each,
// so the cards cascade 1→2→3 even though the backend finishes them out of
// order — and the ignite glows never all fire at once. When several finished
// cards are queued up behind a slow one, drain the backlog on the faster beat
// so a burst doesn't feel sluggish.
const REVEAL_STAGGER_MS = 350
const REVEAL_STAGGER_FAST_MS = 150
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

const EXAMPLE_QUERIES = [
  'a weekend-scale ML project on anomaly detection — I have 4 years of backend experience',
  'papers I could reproduce to learn reinforcement learning, nothing that needs a GPU cluster',
  'recent LLM-agent papers with public code that a solo developer could extend',
  'security research that would make a strong portfolio piece',
  'time-series forecasting methods I could demo with free market data',
]

interface AnalyzingState {
  total: number
  done: number
}

interface DiscoverProps {
  llmSettings: LlmSettingsView | null
  me: MeView | null
  signedOut: boolean
  onSignIn: () => void
  /** Re-fetches the account so the budget chip reflects a just-spent query. */
  refreshMe: () => void
}

export function Discover({ llmSettings, me, signedOut, onSignIn, refreshMe }: DiscoverProps) {
  const [query, setQuery] = useState('')
  const [lastCompiledQuery, setLastCompiledQuery] = useState<string | null>(null)
  const [plan, setPlanState] = useState<SearchPlan | null>(null)
  const [result, setResult] = useState<SearchResult | null>(null)
  const [busy, setBusy] = useState<'compile' | 'search' | null>(null)
  const [analyzing, setAnalyzing] = useState<AnalyzingState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [sortBy, setSortBy] = useState<'match' | 'score'>('match')
  const [analyzeN, setAnalyzeN] = useState(ANALYZE_DEFAULT)
  const [analyzedOnly, setAnalyzedOnly] = useState(false)
  const [hiddenIds, setHiddenIds] = useState<Set<string>>(new Set())
  const [recentSearches, setRecentSearches] = useState<RecentSearchSummary[]>([])
  const [activeSearchId, setActiveSearchId] = useState<number | null>(null)
  // arXiv ids whose analysis just landed — drives the one-shot "ignite" glow.
  const [glowIds, setGlowIds] = useState<ReadonlySet<string>>(new Set())
  // Standing time-gate preference for NEW searches: 'auto' defers to the
  // compiler's inference, null forces any-time, a number forces last-N-days.
  // The per-search chip still edits the current plan independently.
  const [presetWindow, setPresetWindow] = useState<'auto' | number | null>('auto')

  // Funnel-stage copy that steps forward while the first search runs; the
  // ambient ember field (App-level) provides the visual feedback.
  const [searchStage, setSearchStage] = useState(0)
  const searching = busy === 'search'
  useEffect(() => {
    if (!searching) return
    setSearchStage(0)
    const id = window.setInterval(
      () => setSearchStage((s) => Math.min(s + 1, SEARCH_STAGES.length - 1)),
      1700,
    )
    return () => window.clearInterval(id)
  }, [searching])

  const placeholder = useTypingPlaceholder(EXAMPLE_QUERIES, query === '')

  // The poll loop refreshes whatever plan is current when it finishes, even
  // if the user edited chips mid-analysis.
  const planRef = useRef<SearchPlan | null>(null)
  function setPlan(next: SearchPlan | null) {
    planRef.current = next
    setPlanState(next)
  }

  // The prose behind the current plan, read at execute time (a ref, not
  // state, so long-lived poll loops don't capture a stale value). Sent with
  // every search so telemetry can trace results back to the original intent.
  const queryTextRef = useRef<string | null>(null)

  // Compile + analysis spend tokens: they need a signed-in, activated account
  // (locally the server runs as the dev admin, so this is always true there).
  const canSpend = me?.isActive === true
  const signedIn = !signedOut

  // Recent-search history (side panel). Loaded once signed in and refreshed
  // after each executed search.
  const refreshRecent = useCallback(() => {
    if (!signedIn) return
    getRecentSearches()
      .then(setRecentSearches)
      .catch(() => {
        /* history is a convenience; a failure just leaves the panel as-is */
      })
  }, [signedIn])

  useEffect(() => {
    refreshRecent()
  }, [refreshRecent])

  // DTOs for a set of arXiv ids in ONE round-trip (batched — the batch flow
  // fetches every newly-completed paper per poll tick, not one call per paper).
  // Returns null on a failed request so the caller can retry those ids later.
  const fetchPapers = useCallback(async (ids: string[]): Promise<Record<string, PaperDto> | null> => {
    if (ids.length === 0) return {}
    try {
      return await getPapersByIds(ids)
    } catch {
      return null
    }
  }, [])

  // Folds one already-fetched paper into the live result (no re-rank, no
  // network) and fires its ignite glow.
  const revealPaper = useCallback((paper: PaperDto) => {
    setResult((prev) =>
      prev
        ? {
            ...prev,
            hits: prev.hits.map((h) => (h.paper.arxivId === paper.arxivId ? { ...h, paper } : h)),
          }
        : prev,
    )
    setGlowIds((prev) => new Set([...prev, paper.arxivId]))
    window.setTimeout(() => {
      setGlowIds((prev) => {
        const next = new Set(prev)
        next.delete(paper.arxivId)
        return next
      })
    }, 2000)
  }, [])

  // Per-paper analysis: only the budget needs refreshing here — the reveal +
  // glow are handled by mergeAnalyzedPapers below.
  const { analyzingIds, analyzeOne } = useAnalyze(() => refreshMe())

  async function handleAnalyzeOne(arxivId: string) {
    const err = await analyzeOne(arxivId, result?.searchEventId)
    if (err) {
      setError(err)
      return
    }
    const dtos = await fetchPapers([arxivId])
    if (dtos?.[arxivId]) revealPaper(dtos[arxivId])
  }

  async function handleSelectRecent(searchEventId: number) {
    setError(null)
    setNotice(null)
    setBusy('search')
    try {
      const replay = await replaySearch(searchEventId)
      setResult(replay)
      setPlan(replay.plan)
      setHiddenIds(new Set())
      setSortBy('match')
      setActiveSearchId(searchEventId)
      // Prime the dedupe + prose so a follow-up "Refresh" re-runs this plan.
      const summary = recentSearches.find((s) => s.searchEventId === searchEventId)
      queryTextRef.current = summary?.queryText ?? null
      setQuery(summary?.queryText ?? '')
      setLastCompiledQuery(summary?.queryText ?? null)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load that search')
    } finally {
      setBusy(null)
    }
  }

  const analysisEstimate = useMemo(() => {
    if (!llmSettings) return null
    const assignment = llmSettings.assignments.find((a) => a.step === 'PaperAnalysis')
    const model = llmSettings.registry.find((m) => m.id === assignment?.modelId)
    if (!model) return null
    const perPaper =
      (llmSettings.estAnalysisInputTokensPerPaper * model.inputPerMTok +
        llmSettings.estAnalysisOutputTokensPerPaper * model.outputPerMTok) /
      1_000_000
    return { model, perPaper }
  }, [llmSettings])

  async function executePlan(nextPlan: SearchPlan) {
    setBusy('search')
    setError(null)
    try {
      const searchResult = await runSearch(nextPlan, RESULT_LIMIT, queryTextRef.current)
      setResult(searchResult)
      setPlan(searchResult.plan)
      setHiddenIds(new Set())
      setActiveSearchId(searchResult.searchEventId)
      refreshRecent()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Search failed')
    } finally {
      setBusy(null)
    }
  }

  /** The user's standing time-gate choice beats the compiler's inference. */
  function applyPresetWindow(p: SearchPlan): SearchPlan {
    return presetWindow === 'auto' ? p : { ...p, dateWindowDays: presetWindow }
  }

  async function handleSearch(overrideQuery?: string) {
    const trimmed = (overrideQuery ?? query).trim()
    if (!trimmed) return

    // Re-running the same text re-executes the existing plan — deterministic
    // and free. Only genuinely new text goes back through the LLM compiler.
    if (trimmed === lastCompiledQuery && planRef.current) {
      const next = applyPresetWindow(planRef.current)
      setPlan(next)
      await executePlan(next)
      return
    }

    setBusy('compile')
    setError(null)
    setNotice(null)
    try {
      const compiled = applyPresetWindow(await compileSearch(trimmed))
      refreshMe() // compilation spent tokens — update the budget chip
      setLastCompiledQuery(trimmed)
      queryTextRef.current = trimmed
      setPlan(compiled)
      await executePlan(compiled)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not compile the search')
      setBusy(null)
    }
  }

  function tryExample(example: string) {
    setQuery(example)
    void handleSearch(example)
  }

  function updatePlan(patch: Partial<SearchPlan>) {
    if (!plan) return
    const next = { ...plan, ...patch }
    setPlan(next)
    void executePlan(next)
  }

  async function handleAnalyzeTop() {
    if (!result || analyzing) return
    const ids = result.hits.slice(0, analyzeCount).map((h) => h.paper.arxivId)
    setError(null)
    setNotice(null)
    try {
      await analyzeSelection(ids, result.searchEventId)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not queue analysis')
      return
    }

    setAnalyzing({ total: ids.length, done: 0 })
    void pollAnalysis(ids)
  }

  async function pollAnalysis(ids: string[]) {
    // Reveal completed analyses in rank order with a stagger. A cursor walks
    // the ranks: reveal rank N only once its DTO is in hand; if a later rank
    // finishes first, hold it until its turn. Only once polling ends (nothing
    // more will complete) do we skip past ranks that were declined/failed.
    // DTOs are batch-fetched once per poll tick (not one call per paper).
    const cache: Record<string, PaperDto> = {}
    const fetched = new Set<string>()
    const analyzedIds = new Set<string>()
    const inFlight: Promise<void>[] = []
    let cursor = 0
    let finalMode = false
    let chain: Promise<void> = Promise.resolve()

    async function drain() {
      while (cursor < ids.length) {
        const dto = cache[ids[cursor]]
        if (dto) {
          revealPaper(dto)
          cursor++
          if (cursor < ids.length) {
            let buffered = 0
            for (let i = cursor; i < ids.length && cache[ids[i]]; i++) buffered++
            await sleep(buffered >= 3 ? REVEAL_STAGGER_FAST_MS : REVEAL_STAGGER_MS)
          }
        } else if (finalMode) {
          cursor++ // declined/failed or hydration miss — skip so the rest reveal
        } else {
          break // still analyzing (or its fetch is mid-flight); wait for the next poll
        }
      }
    }

    // Drains are serialized on a promise chain so a poll tick landing while a
    // stagger sleep is mid-flight can never swallow the final pass (a dropped
    // final pass would leave declined ranks blocking every card behind them).
    function pump(final: boolean): Promise<void> {
      if (final) finalMode = true
      chain = chain.then(drain)
      return chain
    }

    const finalDone = await pollUntilAnalyzed(ids, (analyzed) => {
      setAnalyzing({ total: ids.length, done: analyzed.length })
      analyzed.forEach((id) => analyzedIds.add(id))
      const toFetch = ids.filter((id) => analyzedIds.has(id) && !fetched.has(id))
      if (toFetch.length > 0) {
        toFetch.forEach((id) => fetched.add(id))
        inFlight.push(
          fetchPapers(toFetch).then((dtos) => {
            if (dtos) Object.assign(cache, dtos)
            else toFetch.forEach((id) => fetched.delete(id)) // failed — retry on a later tick
            void pump(false)
          }),
        )
      } else {
        void pump(false)
      }
    })

    // Polling has ended, but the last tick's DTO fetch may still be in flight —
    // wait for it, then re-fetch anything analyzed whose fetch failed, so the
    // final pass only skips papers that genuinely have no analysis.
    await Promise.all(inFlight)
    const missing = [...analyzedIds].filter((id) => !cache[id])
    if (missing.length > 0) {
      const dtos = await fetchPapers(missing)
      if (dtos) Object.assign(cache, dtos)
    }
    await pump(true)
    setAnalyzing(null)
    refreshMe() // analysis spent tokens — update the budget chip
    setNotice(
      finalDone >= ids.length
        ? `All ${ids.length} papers analyzed — sort by “Analysis score” to lead with the best.`
        : `${finalDone} of ${ids.length} papers analyzed (the rest were declined, failed, or timed out).`,
    )
    // The incremental merge already revealed each card in place; leave the
    // order alone so the reveal isn't yanked out from under the user. They can
    // switch to score sort from the header when they're ready.
  }

  const displayedHits = useMemo(() => {
    if (!result) return []
    // Rank is assigned from the ORIGINAL result order before any client-side
    // filter/sort — it's the position the ranker chose, which is what
    // interaction telemetry must record.
    let hits = result.hits.map((h, i) => ({ ...h, rank: i + 1 }))
    if (hiddenIds.size > 0) {
      hits = hits.filter((h) => !hiddenIds.has(h.paper.arxivId))
    }
    if (analyzedOnly) {
      hits = hits.filter((h) => h.paper.analysis !== null)
    }
    if (sortBy === 'score') {
      hits = [...hits].sort((a, b) => {
        const scoreA = a.paper.analysis?.compositeScore ?? -1
        const scoreB = b.paper.analysis?.compositeScore ?? -1
        return scoreB - scoreA || b.matchScore - a.matchScore
      })
    }
    return hits
  }, [result, sortBy, analyzedOnly, hiddenIds])

  // Offer 5/10/15/20/25, capped at how many results there are; when fewer
  // than 5 exist, the only choice is "all of them".
  const hitCount = result?.hits.length ?? 0
  const analyzeChoices = ANALYZE_OPTIONS.filter((n) => n <= hitCount)
  if (analyzeChoices.length === 0 && hitCount > 0) analyzeChoices.push(hitCount)
  const analyzeCount = Math.min(analyzeN, hitCount)
  const estimateText =
    analysisEstimate && analyzeCount > 0
      ? ` — est. $${(analysisEstimate.perPaper * analyzeCount).toFixed(2)} with ${analysisEstimate.model.displayName}`
      : ''

  return (
    <div className="discover-layout">
      <div className="discover">
      <div className="search-box">
        <textarea
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
              e.preventDefault()
              void handleSearch()
            }
          }}
          placeholder={placeholder}
          rows={2}
          aria-label="Describe what you want to build"
        />
        <label className="time-gate">
          <span>time</span>
          <select
            value={presetWindow === 'auto' ? 'auto' : (presetWindow ?? '')}
            onChange={(e) =>
              setPresetWindow(
                e.target.value === 'auto'
                  ? 'auto'
                  : e.target.value === ''
                    ? null
                    : Number(e.target.value),
              )
            }
            aria-label="Time window for new searches"
            title="Restrict new searches to a publication window — auto lets the search planner decide"
          >
            <option value="auto">auto</option>
            <option value="">any time</option>
            <option value="7">last week</option>
            <option value="30">last month</option>
            <option value="90">last 90 days</option>
            <option value="365">last year</option>
            <option value="1095">last 3 years</option>
            <option value="1825">last 5 years</option>
            <option value="3650">last 10 years</option>
          </select>
        </label>
        <button
          type="button"
          className="primary-button"
          disabled={busy !== null || !query.trim() || !canSpend}
          onClick={() => void handleSearch()}
        >
          {busy === 'compile'
            ? 'Planning…'
            : query.trim() === lastCompiledQuery && plan
              ? 'Refresh'
              : 'Search'}
        </button>
      </div>

      {signedOut && (
        <p className="status">
          <button type="button" className="link-button" onClick={onSignIn}>
            Sign in
          </button>{' '}
          to search — every account gets a free search &amp; analysis budget. Browsing papers
          works without an account.
        </p>
      )}

      {busy === 'compile' && (
        <p className="working-line">
          <EmberDots /> Reading your goal and planning research topics…
        </p>
      )}

      {plan && (
        <div className="plan-panel">
          <p className="plan-interpretation">
            <strong>Searching for:</strong> {plan.interpretation}
          </p>
          <p className="plan-chips-caption">
            {plan.categories.length > 0
              ? 'Limited to these fields (hover to see what each covers, × to drop one):'
              : 'Searching every field — no category filter applied.'}
          </p>
          <div className="plan-chips">
            {plan.categories.map((code) => (
              <span key={code} className="chip chip-category" title={categoryGloss(code)}>
                <span className="chip-code">{code}</span>
                <button
                  type="button"
                  aria-label={`Remove ${code}`}
                  onClick={() =>
                    updatePlan({ categories: plan.categories.filter((c) => c !== code) })
                  }
                >
                  ×
                </button>
              </span>
            ))}
            <span className="chip">
              window:{' '}
              <select
                value={plan.dateWindowDays ?? ''}
                onChange={(e) =>
                  updatePlan({
                    dateWindowDays: e.target.value === '' ? null : Number(e.target.value),
                  })
                }
              >
                <option value="">any time</option>
                <option value="7">last week</option>
                <option value="30">last month</option>
                <option value="90">last 90 days</option>
                <option value="365">last year</option>
                <option value="1095">last 3 years</option>
                <option value="1825">last 5 years</option>
                <option value="3650">last 10 years</option>
              </select>
            </span>
            <label className="chip chip-toggle">
              <input
                type="checkbox"
                checked={plan.requireNoCode === true}
                onChange={(e) => updatePlan({ requireNoCode: e.target.checked ? true : null })}
              />
              no public code (reproduction gap)
            </label>
          </div>
          <details className="plan-anchor">
            <summary>Topics driving the match</summary>
            <textarea
              value={plan.anchorText}
              onChange={(e) => setPlan({ ...plan, anchorText: e.target.value })}
              onBlur={() => planRef.current && void executePlan(planRef.current)}
              rows={3}
            />
          </details>
        </div>
      )}

      {error && <p className="status status-error">{error}</p>}
      {notice && <p className="status status-notice">{notice}</p>}

      {analyzing && (
        <div className="analysis-progress">
          <div className="analysis-progress-track">
            <div
              className="analysis-progress-fill"
              style={{ width: `${Math.round((analyzing.done / analyzing.total) * 100)}%` }}
            />
          </div>
          <span>
            <EmberDots /> Analyzing {analyzing.done}/{analyzing.total} papers… results refresh
            automatically when done
          </span>
        </div>
      )}

      {/* First search in flight: staged funnel copy over skeletons; the
          background ember field carries the motion. */}
      {busy === 'search' && !result && (
        <>
          <p className="working-line">
            <EmberDots /> {SEARCH_STAGES[searchStage]}
          </p>
          <PaperSkeletons count={4} />
        </>
      )}

      {!result && busy === null && (
        <div className="discover-intro">
          <div className="discover-steps">
            <div className="discover-step">
              <span className="discover-step-number">1</span>
              <h3>Describe the build</h3>
              <p>
                Plain language works — mention your experience, your goals, and how much time
                you have.
              </p>
            </div>
            <div className="discover-step">
              <span className="discover-step-number">2</span>
              <h3>We rank the corpus</h3>
              <p>
                Tens of thousands of live arXiv papers, ranked by meaning and exact terms — with
                two deliberate wildcards from outside your comfort zone.
              </p>
            </div>
            <div className="discover-step">
              <span className="discover-step-number">3</span>
              <h3>Analyze your picks</h3>
              <p>
                For the papers you choose, get a personal feasibility read: what you'd learn,
                how long it takes, what it says on a resume.
              </p>
            </div>
          </div>
          <div className="discover-examples">
            <span>Try one:</span>
            {EXAMPLE_QUERIES.slice(0, 3).map((example) => (
              <button
                key={example}
                type="button"
                className="example-chip"
                disabled={!canSpend || busy !== null}
                onClick={() => tryExample(example)}
              >
                {example.length > 64 ? `${example.slice(0, 64)}…` : example}
              </button>
            ))}
          </div>
        </div>
      )}

      {result && (
        <>
          <div className="results-header">
            <span>
              {displayedHits.length} of {result.totalCandidates.toLocaleString()} matching papers
            </span>
            <div className="results-controls">
              <label>
                Sort{' '}
                <select
                  value={sortBy}
                  onChange={(e) => setSortBy(e.target.value as 'match' | 'score')}
                >
                  <option value="match">Relevance (ranked)</option>
                  <option value="score">Analysis score</option>
                </select>
              </label>
              <label className="toolbar-toggle">
                <input
                  type="checkbox"
                  checked={analyzedOnly}
                  onChange={(e) => setAnalyzedOnly(e.target.checked)}
                />{' '}
                Analyzed only
              </label>
              {canSpend && analyzeCount > 0 && (
                <div className="analyze-control">
                  {analyzeChoices.length > 1 && (
                    <label>
                      Top{' '}
                      <select
                        value={analyzeCount}
                        disabled={analyzing !== null}
                        onChange={(e) => setAnalyzeN(Number(e.target.value))}
                      >
                        {analyzeChoices.map((n) => (
                          <option key={n} value={n}>
                            {n}
                          </option>
                        ))}
                      </select>
                    </label>
                  )}
                  <button
                    type="button"
                    className="primary-button"
                    disabled={analyzing !== null}
                    onClick={() => void handleAnalyzeTop()}
                  >
                    {analyzing ? 'Analyzing…' : `Analyze${estimateText}`}
                  </button>
                </div>
              )}
            </div>
          </div>
          {sortBy === 'match' && (
            <p className="results-note">
              Ranked by overall relevance — meaning plus exact keywords. Hover a paper&apos;s
              relevance bar for its rank and semantic-similarity score.
            </p>
          )}
          <div
            className={busy === 'search' ? 'paper-list paper-list-refreshing' : 'paper-list'}
            key={result.searchEventId}
          >
            {displayedHits.map((hit, i) => (
              <div className="ignite" style={{ '--i': i } as React.CSSProperties} key={hit.paper.arxivId}>
                <PaperCard
                  canInteract={canSpend}
                  paper={hit.paper}
                  matchScore={hit.matchScore}
                  rank={hit.rank}
                  rankedCount={result.hits.length}
                  isWildcard={hit.isWildcard}
                  experienceProximity={hit.experienceProximity}
                  searchContext={{ searchEventId: result.searchEventId, rank: hit.rank }}
                  onNotInterested={() =>
                    setHiddenIds((prev) => new Set(prev).add(hit.paper.arxivId))
                  }
                  onAnalyze={() => void handleAnalyzeOne(hit.paper.arxivId)}
                  analyzing={analyzingIds.has(hit.paper.arxivId)}
                  justAnalyzed={glowIds.has(hit.paper.arxivId)}
                />
              </div>
            ))}
          </div>
          {displayedHits.length === 0 && analyzedOnly && result.hits.length > 0 && (
            <p className="status">
              None of these results are analyzed yet — run &quot;Analyze top {analyzeCount}&quot;
              or untick the filter.
            </p>
          )}
          {result.hits.length === 0 && (
            <p className="status">
              No results. If the corpus was just ingested, run the embedding pass
              (<code>dotnet run -- embed</code>) so papers can be ranked.
            </p>
          )}
        </>
      )}
      </div>

      {signedIn && (
        <RecentSearches
          searches={recentSearches}
          activeId={activeSearchId}
          onSelect={(id) => void handleSelectRecent(id)}
        />
      )}
    </div>
  )
}
