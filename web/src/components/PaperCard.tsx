import { useState } from 'react'
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
          <dt>Feasibility</dt>
          <dd>
            {d.feasibility_score}/10 — {d.feasibility_rationale}
          </dd>
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
          <dt>Resume signal</dt>
          <dd>{d.resume_signal}</dd>
        </div>
        <div>
          <dt>Fintech relevance</dt>
          <dd>{d.fintech_relevance_score}/10</dd>
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

export function PaperCard({ paper }: { paper: PaperDto }) {
  const [expanded, setExpanded] = useState(false)
  const [analysisOpen, setAnalysisOpen] = useState(false)

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
    <article className="paper-card">
      <h3>
        <a href={paper.absUrl} target="_blank" rel="noreferrer">
          {paper.title}
        </a>
      </h3>
      <div className="paper-meta">
        {score !== null && (
          <span
            className={`badge badge-score ${scoreTier(score)}`}
            title="Solo-project suitability score (0–100)"
          >
            ★ {Math.round(score)}
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
