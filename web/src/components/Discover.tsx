import { useMemo, useRef, useState } from 'react'
import {
  analyzeSelection,
  compileSearch,
  getAdminKey,
  getAnalysisStatus,
  runSearch,
} from '../api/client'
import type { LlmSettingsView, SearchPlan, SearchResult } from '../api/types'
import { PaperCard } from './PaperCard'

const RESULT_LIMIT = 30
const ANALYZE_TOP_N = 25
const POLL_INTERVAL_MS = 3000
const POLL_TIMEOUT_MS = 6 * 60_000

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

interface AnalyzingState {
  total: number
  done: number
}

interface DiscoverProps {
  llmSettings: LlmSettingsView | null
}

export function Discover({ llmSettings }: DiscoverProps) {
  const [query, setQuery] = useState('')
  const [lastCompiledQuery, setLastCompiledQuery] = useState<string | null>(null)
  const [plan, setPlanState] = useState<SearchPlan | null>(null)
  const [result, setResult] = useState<SearchResult | null>(null)
  const [busy, setBusy] = useState<'compile' | 'search' | null>(null)
  const [analyzing, setAnalyzing] = useState<AnalyzingState | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [sortBy, setSortBy] = useState<'match' | 'score'>('match')
  const [analyzedOnly, setAnalyzedOnly] = useState(false)
  const [hiddenIds, setHiddenIds] = useState<Set<string>>(new Set())

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

  const hasAdminKey = getAdminKey() !== ''

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
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Search failed')
    } finally {
      setBusy(null)
    }
  }

  async function handleSearch() {
    const trimmed = query.trim()
    if (!trimmed) return

    // Re-running the same text re-executes the existing plan — deterministic
    // and free. Only genuinely new text goes back through the LLM compiler.
    if (trimmed === lastCompiledQuery && planRef.current) {
      await executePlan(planRef.current)
      return
    }

    setBusy('compile')
    setError(null)
    setNotice(null)
    try {
      const compiled = await compileSearch(trimmed)
      setLastCompiledQuery(trimmed)
      queryTextRef.current = trimmed
      setPlan(compiled)
      await executePlan(compiled)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not compile the search')
      setBusy(null)
    }
  }

  function updatePlan(patch: Partial<SearchPlan>) {
    if (!plan) return
    const next = { ...plan, ...patch }
    setPlan(next)
    void executePlan(next)
  }

  async function handleAnalyzeTop() {
    if (!result || analyzing) return
    const ids = result.hits.slice(0, ANALYZE_TOP_N).map((h) => h.paper.arxivId)
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
    const started = Date.now()
    let lastDone = -1
    let idleRounds = 0
    let finalDone = 0

    while (Date.now() - started < POLL_TIMEOUT_MS) {
      await sleep(POLL_INTERVAL_MS)
      let status
      try {
        status = await getAnalysisStatus(ids)
      } catch {
        continue // transient poll failure — keep going
      }

      finalDone = status.analyzed.length
      setAnalyzing({ total: ids.length, done: finalDone })

      if (finalDone >= ids.length) break

      // Queue idle and the count stopped moving: the remainder was declined,
      // failed, or was already current — done either way.
      if (!status.active) {
        idleRounds = finalDone === lastDone ? idleRounds + 1 : 0
        if (idleRounds >= 2) break
      } else {
        idleRounds = 0
      }
      lastDone = finalDone
    }

    setAnalyzing(null)
    setNotice(
      finalDone >= ids.length
        ? `All ${ids.length} papers analyzed.`
        : `${finalDone} of ${ids.length} papers analyzed (the rest were declined, failed, or timed out).`,
    )

    // Refresh scores in place by re-executing the current plan — no LLM call,
    // deterministic, so the result list stays stable. Then lead with the
    // best-scored projects, which is what the user paid for.
    if (planRef.current) {
      await executePlan(planRef.current)
    }
    setSortBy('score')
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

  const analyzeCount = Math.min(ANALYZE_TOP_N, result?.hits.length ?? 0)
  const estimateText =
    analysisEstimate && analyzeCount > 0
      ? ` — est. $${(analysisEstimate.perPaper * analyzeCount).toFixed(2)} with ${analysisEstimate.model.displayName}`
      : ''

  return (
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
          placeholder='Describe what you&apos;re after — e.g. "projects to boost my chances at fintech companies when moving to NYC; I have 3 years fullstack experience"'
          rows={2}
        />
        <button
          type="button"
          disabled={busy !== null || !query.trim() || !hasAdminKey}
          onClick={() => void handleSearch()}
        >
          {busy === 'compile'
            ? 'Compiling…'
            : query.trim() === lastCompiledQuery && plan
              ? 'Refresh'
              : 'Search'}
        </button>
      </div>

      {!hasAdminKey && (
        <p className="status">
          Smart search compiles your query with an LLM, which needs the admin API key — set it in
          Settings. (Executing an already-compiled plan is free and key-less.)
        </p>
      )}

      {plan && (
        <div className="plan-panel">
          <p className="plan-interpretation">
            <strong>Searching for:</strong> {plan.interpretation}
          </p>
          <div className="plan-chips">
            {plan.categories.map((code) => (
              <span key={code} className="chip">
                {code}
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
      {busy === 'search' && !result && <p className="status">Ranking…</p>}

      {analyzing && (
        <div className="analysis-progress">
          <div className="analysis-progress-track">
            <div
              className="analysis-progress-fill"
              style={{ width: `${Math.round((analyzing.done / analyzing.total) * 100)}%` }}
            />
          </div>
          <span>
            Analyzing {analyzing.done}/{analyzing.total} papers… results refresh automatically
            when done
          </span>
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
                  <option value="match">Best match</option>
                  <option value="score">Best project score</option>
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
              {hasAdminKey && analyzeCount > 0 && (
                <button
                  type="button"
                  disabled={analyzing !== null}
                  onClick={() => void handleAnalyzeTop()}
                >
                  {analyzing ? 'Analyzing…' : `Analyze top ${analyzeCount}${estimateText}`}
                </button>
              )}
            </div>
          </div>
          <div className={busy === 'search' ? 'paper-list paper-list-refreshing' : 'paper-list'}>
            {displayedHits.map((hit) => (
              <PaperCard
                key={hit.paper.arxivId}
                paper={hit.paper}
                matchScore={hit.matchScore}
                isWildcard={hit.isWildcard}
                experienceProximity={hit.experienceProximity}
                searchContext={{ searchEventId: result.searchEventId, rank: hit.rank }}
                onNotInterested={() =>
                  setHiddenIds((prev) => new Set(prev).add(hit.paper.arxivId))
                }
              />
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
  )
}
