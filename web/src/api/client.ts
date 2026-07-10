import type {
  CategoryDto,
  LlmSettingsView,
  PagedResult,
  PaperDto,
  ProfileView,
  SearchContext,
  SearchPlan,
  SearchResult,
  SortOrder,
} from './types'

const ADMIN_KEY_STORAGE = 'researchDiscovery.adminKey'

export function getAdminKey(): string {
  return localStorage.getItem(ADMIN_KEY_STORAGE) ?? ''
}

export function setAdminKey(key: string) {
  if (key) {
    localStorage.setItem(ADMIN_KEY_STORAGE, key)
  } else {
    localStorage.removeItem(ADMIN_KEY_STORAGE)
  }
}

function adminHeaders(): Record<string, string> {
  const key = getAdminKey()
  return key ? { 'X-Admin-Api-Key': key } : {}
}

async function parseError(response: Response): Promise<Error> {
  let detail = `${response.status} ${response.statusText}`
  try {
    const body = await response.json()
    if (typeof body?.detail === 'string') detail = body.detail
  } catch {
    // Non-JSON error body — keep the status text.
  }
  if (response.status === 404 && getAdminKey() === '') {
    detail = 'Admin features are disabled. Set the admin API key in Settings.'
  }
  if (response.status === 401) {
    detail = 'Admin API key is incorrect. Update it in Settings.'
  }
  return new Error(detail)
}

async function getJson<T>(url: string, signal?: AbortSignal, admin = false): Promise<T> {
  const response = await fetch(url, {
    signal,
    headers: admin ? adminHeaders() : undefined,
  })
  if (!response.ok) throw await parseError(response)
  return (await response.json()) as T
}

async function sendJson<T>(
  method: 'POST' | 'PUT',
  url: string,
  body: unknown,
  options: { admin?: boolean; signal?: AbortSignal } = {},
): Promise<T> {
  const response = await fetch(url, {
    method,
    signal: options.signal,
    headers: {
      'Content-Type': 'application/json',
      ...(options.admin ? adminHeaders() : {}),
    },
    body: JSON.stringify(body),
  })
  if (!response.ok) throw await parseError(response)
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

// --- Browse ---

export interface GetPapersParams {
  categories: string[]
  page: number
  pageSize: number
  sort: SortOrder
  analyzedOnly: boolean
  bookmarkedOnly: boolean
}

export function getPapers(
  { categories, page, pageSize, sort, analyzedOnly, bookmarkedOnly }: GetPapersParams,
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
  if (analyzedOnly) {
    query.set('analyzedOnly', 'true')
  }
  if (bookmarkedOnly) {
    query.set('bookmarkedOnly', 'true')
  }
  return getJson(`/api/papers?${query.toString()}`, signal)
}

export async function setBookmark(
  arxivId: string,
  bookmarked: boolean,
  context?: SearchContext,
): Promise<void> {
  const query = context
    ? `?searchEventId=${context.searchEventId}&rank=${context.rank}`
    : ''
  const response = await fetch(
    `/api/papers/${encodeURIComponent(arxivId)}/bookmark${query}`,
    { method: bookmarked ? 'PUT' : 'DELETE' },
  )
  if (!response.ok) throw await parseError(response)
}

export function getCategories(signal?: AbortSignal): Promise<CategoryDto[]> {
  return getJson('/api/categories', signal)
}

// --- Smart search ---

export function compileSearch(query: string, signal?: AbortSignal): Promise<SearchPlan> {
  return sendJson('POST', '/api/search/compile', { query }, { admin: true, signal })
}

export function runSearch(
  plan: SearchPlan,
  limit: number,
  queryText?: string | null,
  signal?: AbortSignal,
): Promise<SearchResult> {
  return sendJson('POST', '/api/search', { plan, limit, queryText }, { signal })
}

// --- Analysis ---

export function analyzeSelection(
  arxivIds: string[],
  searchEventId?: number,
): Promise<{ message: string }> {
  return sendJson(
    'POST',
    '/api/admin/analysis/selection',
    { arxivIds, searchEventId },
    { admin: true },
  )
}

export interface AnalysisStatus {
  active: boolean
  analyzed: string[]
}

export function getAnalysisStatus(
  arxivIds: string[],
  signal?: AbortSignal,
): Promise<AnalysisStatus> {
  return sendJson('POST', '/api/papers/analysis-status', { arxivIds }, { signal })
}

// --- Settings ---

export function getLlmSettings(signal?: AbortSignal): Promise<LlmSettingsView> {
  return getJson('/api/admin/settings/llm', signal, true)
}

export function setLlmAssignment(step: string, modelId: string): Promise<void> {
  return sendJson('PUT', '/api/admin/settings/llm', { step, modelId }, { admin: true })
}

export function getProfile(signal?: AbortSignal): Promise<ProfileView> {
  return getJson('/api/admin/settings/profile', signal, true)
}

export function saveProfile(
  experienceSummary: string,
  goals: string,
  weeklyHours: number | null,
): Promise<ProfileView> {
  return sendJson(
    'PUT',
    '/api/admin/settings/profile',
    { experienceSummary, goals, weeklyHours },
    { admin: true },
  )
}
