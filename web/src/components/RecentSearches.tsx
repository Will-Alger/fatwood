import { useMemo, useState } from 'react'
import type { RecentSearchSummary } from '../api/types'

interface RecentSearchesProps {
  searches: RecentSearchSummary[]
  activeId: number | null
  onSelect: (searchEventId: number) => void
}

function relativeTime(iso: string): string {
  const seconds = Math.round((Date.now() - new Date(iso).getTime()) / 1000)
  if (seconds < 60) return 'just now'
  const minutes = Math.round(seconds / 60)
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  return `${Math.round(hours / 24)}d ago`
}

/**
 * The signed-in user's recent searches. Clicking one replays its exact result
 * set (no re-ranking). Every executed search is logged — including chip edits
 * and score refreshes — so entries are de-duplicated by their query text,
 * keeping the most recent of each (the list arrives newest-first).
 */
export function RecentSearches({ searches, activeId, onSelect }: RecentSearchesProps) {
  // Collapsed by default on mobile; the toggle only shows there (CSS).
  const [open, setOpen] = useState(false)

  const deduped = useMemo(() => {
    const seen = new Set<string>()
    return searches.filter((s) => {
      const key = (s.queryText ?? s.interpretation).trim().toLowerCase()
      if (seen.has(key)) return false
      seen.add(key)
      return true
    })
  }, [searches])

  return (
    <aside className="recent-panel">
      <button
        type="button"
        className="recent-panel-toggle"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        Recent searches {deduped.length > 0 ? `(${deduped.length})` : ''}
        <span aria-hidden="true">{open ? ' ▲' : ' ▼'}</span>
      </button>

      <div className={open ? 'recent-panel-body recent-open' : 'recent-panel-body'}>
        <h2 className="recent-panel-title">Recent searches</h2>
        {deduped.length === 0 ? (
          <p className="recent-empty">Your searches will show up here — click one to reopen it.</p>
        ) : (
          <ul className="recent-list">
            {deduped.map((s) => (
              <li key={s.searchEventId}>
                <button
                  type="button"
                  className={
                    s.searchEventId === activeId ? 'recent-item recent-item-active' : 'recent-item'
                  }
                  onClick={() => onSelect(s.searchEventId)}
                  title={s.interpretation}
                >
                  <span className="recent-item-text">{s.queryText || s.interpretation}</span>
                  <span className="recent-item-meta">
                    {relativeTime(s.createdUtc)} · {s.resultCount} results
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </aside>
  )
}
