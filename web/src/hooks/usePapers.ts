import { useEffect, useState } from 'react'
import { getPapers } from '../api/client'
import type { PagedResult, PaperDto, SortOrder } from '../api/types'

interface UsePapersState {
  data: PagedResult<PaperDto> | null
  loading: boolean
  error: string | null
}

export function usePapers(
  categories: string[],
  page: number,
  pageSize: number,
  sort: SortOrder,
  analyzedOnly: boolean,
): UsePapersState {
  const [state, setState] = useState<UsePapersState>({
    data: null,
    loading: true,
    error: null,
  })

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
  }, [categoriesKey, page, pageSize, sort, analyzedOnly])

  return state
}
