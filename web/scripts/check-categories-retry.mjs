/**
 * Behavioural check for the retry loop in `src/hooks/useCategories.ts`.
 *
 *     node scripts/check-categories-retry.mjs     # from web/, node >= 22.18
 *
 * Run on demand, not in CI: the frontend CI gate is `npm ci && npm run build`
 * and there is no web test runner in the repo, so `tsc` is the only automatic
 * enforcement web code gets — and a retry loop is exactly the kind of thing a
 * type checker cannot see. Without something like this, "it retries a 500 but
 * not a 404, and stops on unmount" is a claim in a comment.
 *
 * It imports the real hook module — no copy, no reimplementation — and swaps
 * its two runtime dependencies via `module.registerHooks`: `react` for a
 * one-render stand-in that runs effects synchronously and records every
 * `setState`, and `../api/client` for a scripted `getCategories` that records
 * when each attempt arrives. Node's built-in type stripping handles the `.ts`
 * import (the hook is plain erasable TypeScript). Takes ~25s, mostly spent
 * waiting out the real backoff.
 *
 * Exit code is 0 when every assertion holds, 1 otherwise.
 */
import { registerHooks } from 'node:module'

const here = new URL('.', import.meta.url).href
const reactStub = here + 'stubs/react.mjs'
const clientStub = here + 'stubs/api-client.mjs'

registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier === 'react') return { url: reactStub, shortCircuit: true }
    if (specifier.endsWith('api/client')) return { url: clientStub, shortCircuit: true }
    return nextResolve(specifier, context)
  },
})

const react = await import(reactStub)
const client = await import(clientStub)
const { useCategories } = await import(
  new URL('../src/hooks/useCategories.ts', import.meta.url).href,
)

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))
const OK = [{ code: 'cs.LG', name: 'Machine Learning', paperCount: 269_000 }]

let failed = 0

function check(label, ok, detail) {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? `  — ${detail}` : ''}`)
  if (!ok) failed++
}

/**
 * Mount the hook against a scripted sequence of `getCategories` outcomes (the
 * last entry repeats), let it play out, and report what it did.
 */
async function mount(script, { waitMs, unmountAfterMs = null } = {}) {
  react.reset()
  client.setScript(script)
  // Mounting the hook by hand is the whole point of the harness; the stub
  // render loop above is what makes it legal.
  // oxlint-disable-next-line react-hooks/rules-of-hooks
  useCategories()
  if (unmountAfterMs !== null) {
    await sleep(unmountAfterMs)
    react.cleanups.filter(Boolean).forEach((cleanup) => cleanup())
  }
  await sleep(waitMs)
  return {
    calls: client.calls(),
    at: client.callTimes(),
    updates: react.setStateLog.length,
    last: react.setStateLog.at(-1) ?? null,
  }
}

// The shape observed against production on 2026-08-18: the first call to a
// cold replica 500s, the next one succeeds.
let r = await mount([new client.ApiError('500 Internal Server Error', 500), OK], { waitMs: 2000 })
check('a cold-start 500 is retried and the load succeeds', r.calls === 2 && r.last?.error === null && r.last?.categories.length === 1, `calls=${r.calls}`)
check('one state update, so no error flashes before the retry', r.updates === 1, `updates=${r.updates}`)

// Nothing is retried forever, and the error the user finally sees is the real one.
r = await mount([new client.ApiError('500 Internal Server Error', 500)], { waitMs: 9500 })
check('a persistent 500 stops after 1 + 3 attempts', r.calls === 4, `calls=${r.calls}`)
check('the underlying error is surfaced unchanged', r.last?.error === '500 Internal Server Error' && r.last?.categories.length === 0, `error=${r.last?.error}`)
check('backoff is 600 / 2000 / 5000 ms', r.at[1] >= 550 && r.at[2] - r.at[1] >= 1900 && r.at[3] - r.at[2] >= 4900, `at=${r.at.join(',')}`)

// A 4xx is a real answer about the request — repeating it just repeats the answer.
r = await mount([new client.ApiError('404 Not Found', 404)], { waitMs: 1500 })
check('a 404 is not retried', r.calls === 1 && r.last?.error === '404 Not Found', `calls=${r.calls}`)

// ...except the two that explicitly mean "later".
for (const status of [408, 429]) {
  r = await mount([new client.ApiError(`${status}`, status), OK], { waitMs: 2000 })
  check(`a ${status} is retried`, r.calls === 2 && r.last?.error === null, `calls=${r.calls}`)
}

// A rejected fetch never reaches parseError, so it is not an ApiError at all.
r = await mount([new TypeError('Failed to fetch'), OK], { waitMs: 2000 })
check('a rejected fetch is retried', r.calls === 2 && r.last?.error === null, `calls=${r.calls}`)

// Leaving the page mid-backoff must end the loop, not resume it 5s later.
r = await mount([new client.ApiError('500', 500)], { waitMs: 9500, unmountAfterMs: 50 })
check('unmounting during the backoff stops the retries', r.calls === 1, `calls=${r.calls}`)
check('unmounting sets no state', r.updates === 0, `updates=${r.updates}`)

console.log(failed === 0 ? '\nALL PASS' : `\n${failed} FAILED`)
process.exit(failed === 0 ? 0 : 1)
