import { useState } from 'react'
import { setBookmark } from '../api/client'
import type { PaperAnalysisDto, PaperDto } from '../api/types'

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
  isWildcard?: boolean
  experienceProximity?: 'close' | 'stretch' | null
}

export function PaperCard({ paper, matchScore, isWildcard, experienceProximity }: PaperCardProps) {
  const [expanded, setExpanded] = useState(false)
  const [analysisOpen, setAnalysisOpen] = useState(false)
  const [bookmarked, setBookmarked] = useState(paper.isBookmarked)
  const [bookmarkBusy, setBookmarkBusy] = useState(false)

  async function toggleBookmark() {
    const next = !bookmarked
    setBookmarked(next) // optimistic
    setBookmarkBusy(true)
    try {
      await setBookmark(paper.arxivId, next)
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
        {matchScore !== undefined && (
          <span className="badge badge-match" title="Relevance to this search">
            {Math.round(matchScore * 100)}% match
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
      </div>
      {analysisOpen && paper.analysis && <AnalysisPanel analysis={paper.analysis} />}
    </article>
  )
}
