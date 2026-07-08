import type { CategoryDto, PagedResult, PaperDto, SortOrder } from './types'

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, { signal })
  if (!response.ok) {
    throw new Error(`Request failed: ${response.status} ${response.statusText}`)
  }
  return (await response.json()) as T
}

export interface GetPapersParams {
  categories: string[]
  page: number
  pageSize: number
  sort: SortOrder
}

export function getPapers(
  { categories, page, pageSize, sort }: GetPapersParams,
  signal?: AbortSignal,
): Promise<PagedResult<PaperDto>> {
  const query = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
    sort,
  })
  if (categories.length > 0) {
    query.set('categories', categories.join(','))
  }
  return getJson(`/api/papers?${query.toString()}`, signal)
}

export function getCategories(signal?: AbortSignal): Promise<CategoryDto[]> {
  return getJson('/api/categories', signal)
}
