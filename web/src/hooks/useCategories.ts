import { useEffect, useState } from 'react'
import { getCategories } from '../api/client'
import type { CategoryDto } from '../api/types'

interface UseCategoriesState {
  categories: CategoryDto[]
  loading: boolean
  error: string | null
}

export function useCategories(): UseCategoriesState {
  const [state, setState] = useState<UseCategoriesState>({
    categories: [],
    loading: true,
    error: null,
  })

  useEffect(() => {
    const controller = new AbortController()

    getCategories(controller.signal)
      .then((categories) => setState({ categories, loading: false, error: null }))
      .catch((err: unknown) => {
        if (controller.signal.aborted) return
        setState({
          categories: [],
          loading: false,
          error: err instanceof Error ? err.message : 'Failed to load categories',
        })
      })

    return () => controller.abort()
  }, [])

  return state
}
