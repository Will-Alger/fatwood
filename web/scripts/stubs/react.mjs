/**
 * A React stand-in just large enough to mount one hook once, for
 * `scripts/check-categories-retry.mjs`. Not a React implementation: there is a
 * single render, `useEffect` runs its effect immediately and keeps the cleanup
 * so the check can "unmount", and `useState` records every update instead of
 * re-rendering.
 */
let states = []
let cursor = 0

/** Every value passed to a setter, oldest first. */
export const setStateLog = []

/** Cleanups returned by the effects of the current mount. */
export const cleanups = []

export function reset() {
  states = []
  cursor = 0
  setStateLog.length = 0
  cleanups.length = 0
}

export function useState(initial) {
  const slot = cursor++
  if (!(slot in states)) states[slot] = typeof initial === 'function' ? initial() : initial
  return [
    states[slot],
    (value) => {
      states[slot] = value
      setStateLog.push(value)
    },
  ]
}

export function useEffect(effect) {
  cleanups.push(effect())
}
