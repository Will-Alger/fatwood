import { useRef, useState } from 'react'
import { analyzeSelection, getAnalysisStatus } from '../api/client'

// Poll briskly so results reveal close to one-at-a-time as each finishes,
// rather than arriving in clumps between slow polls.
const POLL_INTERVAL_MS = 1200
const POLL_TIMEOUT_MS = 6 * 60_000
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

/**
 * Polls analysis-status for a specific id set until every id is analyzed or the
 * queue goes idle with the count stalled (the remainder was declined, failed,
 * or already current). Returns how many finished. Shared by the batch
 * "Analyze top N" flow and per-paper analysis so they behave identically.
 */
export async function pollUntilAnalyzed(
  ids: string[],
  onTick?: (analyzed: string[], active: boolean) => void,
): Promise<number> {
  const started = Date.now()
  let lastDone = -1
  let idleRounds = 0
  let finalDone = 0

  while (Date.now() - started < POLL_TIMEOUT_MS) {
    await sleep(POLL_INTERVAL_MS)
    let status
    try {
      status = await getAnalysisStatus(ids)
    } catch {
      continue // transient poll failure — keep going
    }

    finalDone = status.analyzed.length
    onTick?.(status.analyzed, status.active)
    if (finalDone >= ids.length) break

    if (!status.active) {
      idleRounds = finalDone === lastDone ? idleRounds + 1 : 0
      if (idleRounds >= 2) break
    } else {
      idleRounds = 0
    }
    lastDone = finalDone
  }

  return finalDone
}

/**
 * Per-paper analysis. Tracks which arXiv ids are currently in flight (so cards
 * can show a spinner) and refreshes via onComplete when one finishes. Coexists
 * with a batch analyze: each call polls only its own id, and the server skips
 * already-current work, so overlapping runs are harmless.
 */
export function useAnalyze(onComplete: () => void | Promise<void>) {
  const [analyzingIds, setAnalyzingIds] = useState<ReadonlySet<string>>(new Set())
  const inFlight = useRef<Set<string>>(new Set())

  function sync() {
    setAnalyzingIds(new Set(inFlight.current))
  }

  /** Returns an error message on failure, or null on success. */
  async function analyzeOne(arxivId: string, searchEventId?: number): Promise<string | null> {
    if (inFlight.current.has(arxivId)) return null
    inFlight.current.add(arxivId)
    sync()

    try {
      await analyzeSelection([arxivId], searchEventId)
    } catch (err) {
      inFlight.current.delete(arxivId)
      sync()
      return err instanceof Error ? err.message : 'Could not queue analysis'
    }

    await pollUntilAnalyzed([arxivId])
    inFlight.current.delete(arxivId)
    sync()
    await onComplete()
    return null
  }

  return { analyzingIds, analyzeOne }
}
