// Check — or refresh — the live-category pin in src/data/liveCategoryCodes.ts
// against what /api/categories actually serves.
//
//   node scripts/category-pin.mjs            # check; exits 1 on drift
//   node scripts/category-pin.mjs --write    # rewrite the union from live
//   node scripts/category-pin.mjs --from cats.json   # a saved response
//
// Why this exists: the pin is what makes a missing gloss a `tsc` failure
// instead of a silent "Physics." on a real corpus category (see the header of
// liveCategoryCodes.ts). But a pin only catches drift when it is refreshed,
// and refreshing it used to mean running a curl, running a node one-liner, and
// hand-pasting 155 lines into the middle of a source file — three steps, each
// of which can go wrong quietly. This is that procedure, executable.
//
// It is deliberately NOT part of `npm run build`: the build gate must not
// depend on the network or on prod being up. Run it when the corpus grows a
// category, or on a steward pass. `--write` only edits the union; if the new
// code has no gloss, `npm run build` is what fails, which is the point.

import { readFileSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

const DEFAULT_SOURCE = 'https://www.fatwood.io/api/categories'
const MARKER = 'export type LiveCategoryCode ='

const here = dirname(fileURLToPath(import.meta.url))
const pinPath = resolve(here, '../src/data/liveCategoryCodes.ts')

const args = process.argv.slice(2)
const write = args.includes('--write')
const fromIndex = args.indexOf('--from')
const source = fromIndex === -1 ? DEFAULT_SOURCE : args[fromIndex + 1]

if (fromIndex !== -1 && !source) {
  console.error('--from needs a file path or URL')
  process.exit(2)
}

/** The codes the pin currently claims, in file order. */
function readPinnedCodes(text) {
  const start = text.indexOf(MARKER)
  if (start === -1) {
    throw new Error(`${pinPath} has no "${MARKER}" — the pin format changed`)
  }
  const union = text.slice(start + MARKER.length)
  return [...union.matchAll(/\|\s*'([^']+)'/g)].map((m) => m[1])
}

/** The codes the corpus serves right now, sorted the way the pin stores them. */
async function readLiveCodes(from) {
  const raw = /^https?:\/\//.test(from)
    ? await fetch(from).then((res) => {
        if (!res.ok) throw new Error(`${from} answered ${res.status} ${res.statusText}`)
        return res.text()
      })
    : readFileSync(from, 'utf8')

  const parsed = JSON.parse(raw)
  const rows = Array.isArray(parsed) ? parsed : parsed.categories
  if (!Array.isArray(rows)) {
    throw new Error(`${from} is not a category list`)
  }
  const codes = rows.map((row) => row.code)
  if (codes.some((code) => typeof code !== 'string' || code.length === 0)) {
    throw new Error(`${from} has a row with no code`)
  }
  // A duplicate would silently shrink the union and un-pin a category.
  const unique = [...new Set(codes)]
  if (unique.length !== codes.length) {
    throw new Error(`${from} lists ${codes.length - unique.length} duplicate code(s)`)
  }
  return unique.sort()
}

/** Everything above the union stays byte-identical; only the members change. */
function renderPin(text, codes) {
  const start = text.indexOf(MARKER)
  const header = text.slice(0, start + MARKER.length)
  return `${header}\n${codes.map((code) => `  | '${code}'`).join('\n')}\n`
}

const pinText = readFileSync(pinPath, 'utf8')
const pinned = readPinnedCodes(pinText)
const live = await readLiveCodes(source)

const added = live.filter((code) => !pinned.includes(code))
const removed = pinned.filter((code) => !live.includes(code))

console.log(`pin ${pinned.length} codes · live ${live.length} codes · ${source}`)

if (added.length === 0 && removed.length === 0) {
  console.log('in sync — nothing to do')
  process.exit(0)
}

for (const code of added) console.log(`  + ${code} (serving papers, not pinned)`)
for (const code of removed) console.log(`  - ${code} (pinned, no longer served)`)

if (!write) {
  console.error(
    `\ndrift: ${added.length} added, ${removed.length} removed.` +
      '\nRe-run with --write, then `npm run build` — a new code with no gloss fails tsc.',
  )
  process.exit(1)
}

writeFileSync(pinPath, renderPin(pinText, live))
console.log(`\nwrote ${live.length} codes to src/data/liveCategoryCodes.ts`)
console.log('now run `npm run build` — an unglossed new code fails tsc, by design')
