import { useState } from 'react'
import { markNotInterested, setBookmark } from '../api/client'
import type { PaperAnalysisDto, PaperDto, SearchContext } from '../api/types'

const ABSTRACT_PREVIEW_LENGTH = 400

const EFFORT_LABELS: Record<string, string> = {
  weekend: 'Weekend',
  one_to_two_weeks: '1–2 weeks',
  about_a_month: 'About a month',
  multi_month: 'Multi-month',
}

function scoreTier(score: number): string {
  if (score >= 80) return 'score-exceptional'
  if (score >= 60) return 'score-strong'
  if (score >= 40) return 'score-workable'
  return 'score-poor'
}

function AnalysisPanel({ analysis }: { analysis: PaperAnalysisDto }) {
  const d = analysis.details
  return (
    <div className="analysis-panel">
      <p className="analysis-summary">{d.summary}</p>
      <dl className="analysis-grid">
        <div>
          <dt>Feasibility for you</dt>
          <dd>
            {d.feasibility_score}/10
            {d.hard_blockers.length > 0
              ? ` — blockers: ${d.hard_blockers.join('; ')}`
              : ' — no hard blockers'}
          </dd>
        </div>
        <div>
          <dt>Learning bridge</dt>
          <dd>{d.learning_bridge}</dd>
        </div>
        <div>
          <dt>Effort</dt>
          <dd>{EFFORT_LABELS[d.estimated_effort] ?? d.estimated_effort}</dd>
        </div>
        <div>
          <dt>Approach</dt>
          <dd>
            {d.approach === 'reproduce' ? 'Reproduce' : 'Extend'} — {d.approach_rationale}
          </dd>
        </div>
        <div>
          <dt>Reference code</dt>
          <dd>{d.reference_code_likelihood} likelihood it already exists</dd>
        </div>
        <div>
          <dt>Goal alignment</dt>
          <dd>{d.goal_alignment_score}/10</dd>
        </div>
        <div>
          <dt>Resume signal</dt>
          <dd>{d.resume_signal}</dd>
        </div>
        <div>
          <dt>Extension idea</dt>
          <dd>{d.extension_idea}</dd>
        </div>
        <div>
          <dt>Skills exercised</dt>
          <dd>{d.required_skills.join(', ')}</dd>
        </div>
      </dl>
      <p className="analysis-provenance">
        Analyzed by {analysis.model} on{' '}
        {new Date(analysis.createdUtc).toLocaleDateString(undefined, {
          year: 'numeric',
          month: 'short',
          day: 'numeric',
        })}
      </p>
    </div>
  )
}

export interface PaperCardProps {
  paper: PaperDto
  matchScore?: number
  /** Best match score in the current result set — match strength is relative to it. */
  topMatchScore?: number
  isWildcard?: boolean
  experienceProximity?: 'close' | 'stretch' | null
  /** Present when this card came from a search — bookmarks then carry (search, rank) telemetry. */
  searchContext?: SearchContext
  /** When set, shows a "not interested" control; the parent removes the card. */
  onNotInterested?: () => void
  /** When set, shows an "Analyze this paper" action (hidden once analyzed). */
  onAnalyze?: () => void
  /** True while this specific paper's analysis is queued/running. */
  analyzing?: boolean
  /** Bookmarks/feedback are per-account writes; false (signed out or gated) hides them. */
  canInteract?: boolean
}

/**
 * Semantic similarity to the query, shown relative to the best hit in this
 * result set (raw cosine values cluster in a narrow band, so absolute
 * percentages mislead). This describes the PAPER, not its rank: the list is
 * ordered by overall relevance (meaning + exact keywords), so a
 * medium-similarity paper can sit above a high-similarity one — by design.
 */
function similarityTier(score: number, top: number): { label: string; level: 1 | 2 | 3 } {
  const ratio = top > 0 ? score / top : 0
  if (ratio >= 0.92) return { label: 'high similarity', level: 3 }
  if (ratio >= 0.78) return { label: 'medium similarity', level: 2 }
  return { label: 'lower similarity', level: 1 }
}

export function PaperCard({
  paper,
  matchScore,
  topMatchScore,
  isWildcard,
  experienceProximity,
  searchContext,
  onNotInterested,
  onAnalyze,
  analyzing = false,
  canInteract = false,
}: PaperCardProps) {
  const [expanded, setExpanded] = useState(false)
  const [analysisOpen, setAnalysisOpen] = useState(false)
  const [bookmarked, setBookmarked] = useState(paper.isBookmarked)
  const [bookmarkBusy, setBookmarkBusy] = useState(false)

  async function toggleBookmark() {
    const next = !bookmarked
    setBookmarked(next) // optimistic
    setBookmarkBusy(true)
    try {
      await setBookmark(paper.arxivId, next, searchContext)
    } catch {
      setBookmarked(!next) // roll back on failure
    } finally {
      setBookmarkBusy(false)
    }
  }

  const score = paper.analysis?.compositeScore ?? null

  const published = new Date(paper.publishedUtc).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })

  const needsClamp = paper.abstract.length > ABSTRACT_PREVIEW_LENGTH
  const abstract =
    expanded || !needsClamp
      ? paper.abstract
      : `${paper.abstract.slice(0, ABSTRACT_PREVIEW_LENGTH).trimEnd()}…`

  return (
    <article className={isWildcard ? 'paper-card paper-card-wildcard' : 'paper-card'}>
      <div className="paper-title-row">
        <h3>
          <a href={paper.absUrl} target="_blank" rel="noreferrer">
            {paper.title}
          </a>
        </h3>
        {canInteract && (
          <button
            type="button"
            className={bookmarked ? 'bookmark-button bookmark-on' : 'bookmark-button'}
            disabled={bookmarkBusy}
            onClick={() => void toggleBookmark()}
            title={bookmarked ? 'Remove bookmark' : 'Bookmark this paper'}
            aria-label={bookmarked ? 'Remove bookmark' : 'Bookmark this paper'}
          >
            {bookmarked ? '★' : '☆'}
          </button>
        )}
        {canInteract && onNotInterested && (
          <button
            type="button"
            className="bookmark-button"
            onClick={() => {
              onNotInterested()
              void markNotInterested(paper.arxivId, searchContext).catch(() => {
                /* telemetry write only — the card is already hidden */
              })
            }}
            title="Not interested — hide and teach the ranker"
            aria-label="Not interested"
          >
            ✕
          </button>
        )}
      </div>
      <div className="paper-meta">
        {isWildcard && (
          <span
            className="badge badge-wildcard"
            title="Outside your usual territory but highly relevant to this search — deliberate serendipity"
          >
            ✦ wildcard
          </span>
        )}
        {score !== null && (
          <span
            className={`badge badge-score ${scoreTier(score)}`}
            title="Personalized project-fit score (0–100)"
          >
            ★ {Math.round(score)}
          </span>
        )}
        {matchScore !== undefined && topMatchScore !== undefined && (
          <span
            className={`match-meter match-level-${similarityTier(matchScore, topMatchScore).level}`}
            title={`Semantic similarity to your search (${Math.round(matchScore * 100)}%). Results are ordered by overall relevance — meaning plus exact keywords — so this bar isn't strictly the sort order.`}
          >
            <span className="match-segments" aria-hidden="true">
              <span />
              <span />
              <span />
            </span>
            {similarityTier(matchScore, topMatchScore).label}
          </span>
        )}
        {experienceProximity === 'close' && (
          <span className="badge badge-close" title="Close to your existing experience">
            close to home
          </span>
        )}
        {experienceProximity === 'stretch' && (
          <span className="badge badge-stretch" title="A stretch beyond your experience — bigger learning bridge">
            stretch
          </span>
        )}
        <span className="paper-date">{published}</span>
        {paper.categories.map((code) => (
          <span
            key={code}
            className={code === paper.primaryCategory ? 'badge badge-primary' : 'badge'}
          >
            {code}
          </span>
        ))}
      </div>
      <p className="paper-authors">{paper.authors.join(', ')}</p>
      <p className="paper-abstract">
        {abstract}{' '}
        {needsClamp && (
          <button type="button" className="link-button" onClick={() => setExpanded(!expanded)}>
            {expanded ? 'Show less' : 'Show more'}
          </button>
        )}
      </p>
      <div className="paper-links">
        <a href={paper.absUrl} target="_blank" rel="noreferrer">
          arXiv:{paper.arxivId}
        </a>
        <a href={paper.pdfUrl} target="_blank" rel="noreferrer">
          PDF
        </a>
        {paper.doi && (
          <a href={`https://doi.org/${paper.doi}`} target="_blank" rel="noreferrer">
            DOI
          </a>
        )}
        {paper.codeUrl && (
          <a href={paper.codeUrl} target="_blank" rel="noreferrer" title="Code advertised by the authors">
            Code ↗
          </a>
        )}
        {paper.analysis && (
          <button
            type="button"
            className="link-button"
            onClick={() => setAnalysisOpen(!analysisOpen)}
          >
            {analysisOpen ? 'Hide project analysis' : 'Project analysis'}
          </button>
        )}
        {canInteract && onAnalyze && !paper.analysis && (
          <button
            type="button"
            className="link-button link-button-analyze"
            disabled={analyzing}
            onClick={onAnalyze}
            title="Get a personalized feasibility read on this paper"
          >
            {analyzing ? 'Analyzing…' : 'Analyze this paper'}
          </button>
        )}
      </div>
      {analysisOpen && paper.analysis && <AnalysisPanel analysis={paper.analysis} />}
    </article>
  )
}
