import { useMemo, useState } from 'react'
import {
  analyzeSelection,
  compileSearch,
  getAdminKey,
  runSearch,
} from '../api/client'
import type { LlmSettingsView, SearchPlan, SearchResult } from '../api/types'
import { PaperCard } from './PaperCard'

const RESULT_LIMIT = 30
const ANALYZE_TOP_N = 25

interface DiscoverProps {
  llmSettings: LlmSettingsView | null
}

export function Discover({ llmSettings }: DiscoverProps) {
  const [query, setQuery] = useState('')
  const [plan, setPlan] = useState<SearchPlan | null>(null)
  const [result, setResult] = useState<SearchResult | null>(null)
  const [busy, setBusy] = useState<'compile' | 'search' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

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
      const searchResult = await runSearch(nextPlan, RESULT_LIMIT)
      setResult(searchResult)
      setPlan(searchResult.plan)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Search failed')
    } finally {
      setBusy(null)
    }
  }

  async function handleCompile() {
    if (!query.trim()) return
    setBusy('compile')
    setError(null)
    setNotice(null)
    try {
      const compiled = await compileSearch(query.trim())
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
    if (!result) return
    const ids = result.hits.slice(0, ANALYZE_TOP_N).map((h) => h.paper.arxivId)
    setError(null)
    try {
      const response = await analyzeSelection(ids)
      setNotice(
        `${response.message} Papers with a current analysis are skipped. ` +
          'Re-run the search in a few minutes to see scores.',
      )
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not queue analysis')
    }
  }

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
              void handleCompile()
            }
          }}
          placeholder='Describe what you&apos;re after — e.g. "projects to boost my chances at fintech companies when moving to NYC; I have 3 years fullstack experience"'
          rows={2}
        />
        <button
          type="button"
          disabled={busy !== null || !query.trim() || !hasAdminKey}
          onClick={() => void handleCompile()}
        >
          {busy === 'compile' ? 'Compiling…' : 'Search'}
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
              onBlur={() => plan && void executePlan(plan)}
              rows={3}
            />
          </details>
        </div>
      )}

      {error && <p className="status status-error">{error}</p>}
      {notice && <p className="status status-notice">{notice}</p>}
      {busy === 'search' && <p className="status">Ranking…</p>}

      {result && busy === null && (
        <>
          <div className="results-header">
            <span>
              {result.hits.length} of {result.totalCandidates.toLocaleString()} matching papers
            </span>
            {hasAdminKey && analyzeCount > 0 && (
              <button type="button" onClick={() => void handleAnalyzeTop()}>
                Analyze top {analyzeCount}
                {estimateText}
              </button>
            )}
          </div>
          <div className="paper-list">
            {result.hits.map((hit) => (
              <PaperCard
                key={hit.paper.arxivId}
                paper={hit.paper}
                matchScore={hit.matchScore}
                isWildcard={hit.isWildcard}
                experienceProximity={hit.experienceProximity}
              />
            ))}
          </div>
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
