/**
 * Stands in for `src/api/client.ts` in `scripts/check-categories-retry.mjs`.
 * `ApiError` mirrors the real one (callers branch on `.status`); `getCategories`
 * replays a scripted sequence of outcomes and records when each attempt landed,
 * which is how the check sees the backoff.
 */
export class ApiError extends Error {
  constructor(message, status) {
    super(message)
    this.status = status
  }
}

let script = []
let attempts = 0
let times = []
let start = 0

/** Outcomes to replay, one per attempt; the last entry repeats. */
export function setScript(outcomes) {
  script = outcomes
  attempts = 0
  times = []
  start = Date.now()
}

export const calls = () => attempts
/** Milliseconds from `setScript` to each attempt. */
export const callTimes = () => times.slice()

export async function getCategories(signal) {
  const index = attempts++
  times.push(Date.now() - start)
  if (signal?.aborted) throw new DOMException('aborted', 'AbortError')
  const outcome = script[Math.min(index, script.length - 1)]
  if (outcome instanceof Error) throw outcome
  return outcome
}
