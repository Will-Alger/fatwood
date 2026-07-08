import { useState } from 'react'
import type { PaperDto } from '../api/types'

const ABSTRACT_PREVIEW_LENGTH = 400

export function PaperCard({ paper }: { paper: PaperDto }) {
  const [expanded, setExpanded] = useState(false)

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
      </div>
    </article>
  )
}
