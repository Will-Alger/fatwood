import { useCallback, useEffect, useState } from 'react'
import { ApiError, getMe } from '../api/client'
import type { MeView } from '../api/types'

export interface MeState {
  /** The signed-in account (or the local-dev admin when the server runs authless). */
  me: MeView | null
  /** True once the first /api/me round-trip has settled. */
  ready: boolean
  /** True when the server answered 401: real auth is on and nobody is signed in. */
  signedOut: boolean
  refresh: () => void
}

/**
 * Account state is driven by the server, not by MSAL: /api/me succeeding
 * without a token means the server is in local-dev mode and the UI should
 * behave signed-in. A 401 means auth is real and the user isn't.
 */
export function useMe(): MeState {
  const [me, setMe] = useState<MeView | null>(null)
  const [ready, setReady] = useState(false)
  const [signedOut, setSignedOut] = useState(false)
  const [nonce, setNonce] = useState(0)

  const refresh = useCallback(() => setNonce((n) => n + 1), [])

  useEffect(() => {
    const controller = new AbortController()
    getMe(controller.signal)
      .then((view) => {
        setMe(view)
        setSignedOut(false)
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setMe(null)
        setSignedOut(error instanceof ApiError && error.status === 401)
      })
      .finally(() => {
        if (!controller.signal.aborted) setReady(true)
      })
    return () => controller.abort()
  }, [nonce])

  return { me, ready, signedOut, refresh }
}

/** "$0.87" style, or "∞" for unlimited accounts. */
export function formatBudget(me: MeView): string {
  if (me.budget.unlimited) return '∞'
  return `$${(me.budget.remainingMicros / 1_000_000).toFixed(2)}`
}
