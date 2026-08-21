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
 * Whether the planner's reading is worth a second line, or is just the query
 * back again. A search logged without prose already renders its interpretation
 * as the row's main text, so repeating it there would be noise; so would an
 * interpretation the compiler echoed verbatim.
 */
function addsSomething(interpretation: string, primary: string): boolean {
  const normalize = (s: string) => s.trim().toLowerCase().replace(/\s+/g, ' ')
  const reading = normalize(interpretation)
  return reading.length > 0 && reading !== normalize(primary)
}

/**
 * The signed-in user's recent searches. Clicking one replays its exact result
 * set (no re-ranking). Every executed search is logged — including chip edits
 * and score refreshes — so entries are de-duplicated by their query text,
 * keeping the most recent of each (the list arrives newest-first).
 *
 * Each row shows both halves of the search: the prose the user typed, and —
 * when it says something the prose does not — how the planner read it. The
 * interpretation used to live only in the row's `title`, i.e. behind a hover,
 * which is no affordance at all on touch or by keyboard; replaying is the only
 * other way to see it and that costs a round-trip. The `title` moved to the
 * query text instead, which is the part that actually truncates
 * (`.recent-item-text` clamps at two lines) and had no tooltip of its own.
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
            {deduped.map((s) => {
              // Falls back to the interpretation exactly as the dedupe key does,
              // so the row is never blank for a search logged without prose.
              const primary = s.queryText || s.interpretation
              const reading = addsSomething(s.interpretation, primary) ? s.interpretation : null
              return (
                <li key={s.searchEventId}>
                  <button
                    type="button"
                    className={
                      s.searchEventId === activeId ? 'recent-item recent-item-active' : 'recent-item'
                    }
                    onClick={() => onSelect(s.searchEventId)}
                  >
                    <span className="recent-item-text" title={primary}>
                      {primary}
                    </span>
                    {reading && <span className="recent-item-reading">{reading}</span>}
                    <span className="recent-item-meta">
                      {relativeTime(s.createdUtc)} · {s.resultCount} results
                    </span>
                  </button>
                </li>
              )
            })}
          </ul>
        )}
      </div>
    </aside>
  )
}
