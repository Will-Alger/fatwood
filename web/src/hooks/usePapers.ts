import { useEffect, useState } from 'react'
import { getPapers } from '../api/client'
import type { PagedResult, PaperDto, SortOrder } from '../api/types'

interface UsePapersState {
  data: PagedResult<PaperDto> | null
  loading: boolean
  error: string | null
  /** Re-fetches the current page (e.g. after analyzing a paper in place). */
  refetch: () => void
}

export function usePapers(
  categories: string[],
  page: number,
  pageSize: number,
  sort: SortOrder,
  analyzedOnly: boolean,
  bookmarkedOnly: boolean,
  windowDays: number | null,
): UsePapersState {
  const [state, setState] = useState<Omit<UsePapersState, 'refetch'>>({
    data: null,
    loading: true,
    error: null,
  })
  const [reloadKey, setReloadKey] = useState(0)

  // Joined key keeps the effect dependency simple and value-based.
  const categoriesKey = categories.join(',')

  useEffect(() => {
    const controller = new AbortController()
    setState((prev) => ({ ...prev, loading: true, error: null }))

    getPapers(
      {
        categories: categoriesKey ? categoriesKey.split(',') : [],
        page,
        pageSize,
        sort,
        analyzedOnly,
        bookmarkedOnly,
        windowDays,
      },
      controller.signal,
    )
      .then((data) => setState({ data, loading: false, error: null }))
      .catch((err: unknown) => {
        if (controller.signal.aborted) return
        setState({
          data: null,
          loading: false,
          error: err instanceof Error ? err.message : 'Failed to load papers',
        })
      })

    return () => controller.abort()
  }, [categoriesKey, page, pageSize, sort, analyzedOnly, bookmarkedOnly, windowDays, reloadKey])

  return { ...state, refetch: () => setReloadKey((k) => k + 1) }
}
