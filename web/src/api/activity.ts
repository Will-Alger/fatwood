/**
 * Global "the app is waiting on the network" signal for ambient UI (the
 * ember field). The API client calls begin/end around every request; the
 * consumer polls waitLevel() from its animation loop, so no React state or
 * re-renders are involved.
 */

let pending = 0
let lastSettled = 0

export function beginWait() {
  pending++
}

export function endWait() {
  pending = Math.max(0, pending - 1)
  lastSettled = performance.now()
}

/** True while any request is in flight — the undecayed signal. */
export function isWaiting(): boolean {
  return pending > 0
}

/**
 * 1 while any request is in flight, decaying to 0 over ~3.5s afterward. The
 * decay bridges the gaps in poll loops (analysis polls every 3s) so an
 * intermittent wait reads as one continuous warm period.
 */
export function waitLevel(now: number): number {
  if (pending > 0) return 1
  if (lastSettled === 0) return 0
  return Math.max(0, 1 - (now - lastSettled) / 3500)
}
