import type { PagedResult, PaperDto } from '../api/types'
import { PaperCard } from './PaperCard'
import { PaperSkeletons } from './Skeletons'

interface PaperListProps {
  data: PagedResult<PaperDto> | null
  loading: boolean
  error: string | null
}

export function PaperList({ data, loading, error }: PaperListProps) {
  if (error) {
    return <p className="status status-error">Could not load papers: {error}</p>
  }

  if (loading && !data) {
    return <PaperSkeletons count={5} />
  }

  if (!data || data.items.length === 0) {
    return (
      <p className="status">
        No papers found. If the database is empty, run the ingestion backfill first.
      </p>
    )
  }

  return (
    <div className={loading ? 'paper-list paper-list-refreshing' : 'paper-list'}>
      {data.items.map((paper) => (
        <PaperCard key={paper.arxivId} paper={paper} />
      ))}
    </div>
  )
}
