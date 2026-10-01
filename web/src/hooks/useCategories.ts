import { useEffect, useState } from 'react'
import { ApiError, getCategories } from '../api/client'
import type { CategoryDto } from '../api/types'

interface UseCategoriesState {
  categories: CategoryDto[]
  loading: boolean
  error: string | null
}

/**
 * Backoff between attempts at `GET /api/categories`. One request is not enough:
 * the API runs on a scale-to-zero container app, and the first request after an
 * idle period can fail outright while the replica is still coming up — observed
 * 2026-08-18 against production, where a cold first call returned 500 and the
 * next three returned 200 (21.3s, 19.0s, 3.9s).
 *
 * Losing that one call is not a cosmetic failure. `categories` is the only
 * source of real field names in the UI: `CategoryFilter` renders nothing at all
 * from an empty list, and `Discover` builds its code → name map from it, so the
 * search-plan chips and the "What these fields mean" block fall back to bare
 * arXiv codes (`q-bio.QM`) — exactly the transparency the Tier 2 B.3 passes
 * were for. There is no in-page recovery either: the request fires once from a
 * mount effect, so the page stays category-less until the user reloads it.
 *
 * ~7.6s of waiting, spread out, in exchange for surviving a cold start.
 */
const RETRY_DELAYS_MS = [600, 2000, 5000]

/**
 * Worth another attempt? A 4xx is a real answer about the request, so retrying
 * it just repeats the failure — except 408/429, which explicitly mean "later".
 * A rejected fetch (offline, DNS, connection reset) never reaches `parseError`
 * and so is not an `ApiError` at all; those are the transient case by default.
 */
function isTransient(err: unknown): boolean {
  if (err instanceof ApiError) {
    return err.status >= 500 || err.status === 408 || err.status === 429
  }
  return true
}

/** Sleep that gives up as soon as the effect is torn down. */
function delay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const done = () => {
      clearTimeout(timer)
      signal.removeEventListener('abort', done)
      resolve()
    }
    const timer = setTimeout(done, ms)
    signal.addEventListener('abort', done, { once: true })
  })
}

export function useCategories(): UseCategoriesState {
  const [state, setState] = useState<UseCategoriesState>({
    categories: [],
    loading: true,
    error: null,
  })

  useEffect(() => {
    const controller = new AbortController()
    const signal = controller.signal

    // State is left untouched between attempts, so a retried load reads as one
    // slow load rather than flashing an error the hook is about to withdraw.
    const load = async () => {
      for (let attempt = 0; ; attempt++) {
        try {
          const categories = await getCategories(signal)
          if (signal.aborted) return
          setState({ categories, loading: false, error: null })
          return
        } catch (err: unknown) {
          if (signal.aborted) return
          if (attempt < RETRY_DELAYS_MS.length && isTransient(err)) {
            await delay(RETRY_DELAYS_MS[attempt], signal)
            if (signal.aborted) return
            continue
          }
          setState({
            categories: [],
            loading: false,
            error: err instanceof Error ? err.message : 'Failed to load categories',
          })
          return
        }
      }
    }

    void load()

    return () => controller.abort()
  }, [])

  return state
}
