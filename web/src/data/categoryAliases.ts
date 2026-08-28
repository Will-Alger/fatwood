import type { CategoryDto } from '../api/types'

/**
 * arXiv files some fields under two codes at once. A numerical-analysis paper
 * is `math.NA` or `cs.NA` depending on where its author submitted it, and the
 * taxonomy gives the pair a single display name ("Numerical Analysis") because
 * it is a single field.
 *
 * That is invisible — and reads as a bug — in the browse list, which sorts by
 * paper count and therefore parks the two halves next to each other as two
 * rows with the same name and different counts. It also matters: the filters
 * match codes literally — `SearchService.FilterCandidates` and
 * `PaperQueryService` both reduce to `codes.Contains(pc.Category.Code)`, and
 * nothing in the backend expands an alias — so a user who ticks `cs.NA` alone
 * is silently not seeing the papers filed as `math.NA`.
 *
 * The pairing is DERIVED from the live `/api/categories` response — codes that
 * share a display name — and never hardcoded. A hardcoded list would be one
 * more table to refresh whenever the corpus grows a category, and the failure
 * mode of a stale one is silence. Prose has already drifted this way: of the
 * ten alias halves the corpus serves today, only four said so in their gloss.
 *
 * Live shape at the time of writing (155 categories, 5 pairs, verified against
 * https://www.fatwood.io/api/categories): cs.IT/math.IT, cs.NA/math.NA,
 * cs.SY/eess.SY, math.MP/math-ph, math.ST/stat.TH.
 */
export function aliasIndex(categories: CategoryDto[]): Map<string, string[]> {
  const codesByName = new Map<string, string[]>()
  for (const category of categories) {
    const codes = codesByName.get(category.name)
    if (codes) codes.push(category.code)
    else codesByName.set(category.name, [category.code])
  }

  const index = new Map<string, string[]>()
  for (const codes of codesByName.values()) {
    if (codes.length < 2) continue
    for (const code of codes) {
      index.set(
        code,
        codes.filter((other) => other !== code),
      )
    }
  }
  return index
}
