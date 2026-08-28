import type { PagedResult, PaperDto } from '../api/types'
import { PaperCard } from './PaperCard'
import { PaperSkeletons } from './Skeletons'

interface PaperListProps {
  data: PagedResult<PaperDto> | null
  loading: boolean
  error: string | null
  canInteract?: boolean
  /** arXiv ids currently being analyzed (per-card spinner). */
  analyzingIds?: ReadonlySet<string>
  /** Triggers analysis for one paper; omitted where analysis isn't offered. */
  onAnalyze?: (arxivId: string) => void
}

export function PaperList({
  data,
  loading,
  error,
  canInteract = false,
  analyzingIds,
  onAnalyze,
}: PaperListProps) {
  if (error) {
    return <p className="status status-error">Could not load papers: {error}</p>
  }

  if (loading && !data) {
    return <PaperSkeletons count={5} />
  }

  if (!data || data.items.length === 0) {
    // This list is the browse view, and every filter above it can empty it:
    // a narrow category, a short date window, Analyzed only, Bookmarked. In
    // production the corpus is never the cause — /api/papers returns zero for
    // ordinary combinations against the full corpus (math.CO alone has 3,509
    // papers; math.CO with windowDays=1 has none) — so blaming an empty
    // database sent users looking in the one place that is never wrong, and
    // handed them an operator instruction they have no way to act on. The
    // backfill hint is real for a fresh local database, so it stays where that
    // is possible: `import.meta.env.DEV` is false in the built SPA prod serves.
    return (
      <p className="status">
        No papers match these filters. Try widening the date window, or clearing a
        category, Analyzed only, or Bookmarked.
        {import.meta.env.DEV &&
          ' If this is a fresh local database, run the ingestion backfill first.'}
      </p>
    )
  }

  return (
    <div className={loading ? 'paper-list paper-list-refreshing' : 'paper-list'}>
      {data.items.map((paper) => (
        <PaperCard
          key={paper.arxivId}
          paper={paper}
          canInteract={canInteract}
          onAnalyze={onAnalyze ? () => onAnalyze(paper.arxivId) : undefined}
          analyzing={analyzingIds?.has(paper.arxivId)}
        />
      ))}
    </div>
  )
}
