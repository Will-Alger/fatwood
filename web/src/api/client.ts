import { getAccessToken } from '../auth/auth'
import type {
  AdminUserView,
  CategoryDto,
  InviteView,
  LlmSettingsView,
  MeView,
  PagedResult,
  PaperDto,
  ProfileView,
  SearchContext,
  SearchPlan,
  SearchResult,
  SortOrder,
} from './types'

/** Thrown for HTTP errors so callers can branch on status (401/402/403). */
export class ApiError extends Error {
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

// When signed in, every call carries the bearer token (harmless on anonymous
// endpoints). Locally with no tenant the server authenticates everything as
// the dev admin, so a missing token is fine there too.
async function authHeaders(): Promise<Record<string, string>> {
  try {
    const token = await getAccessToken()
    return token ? { Authorization: `Bearer ${token}` } : {}
  } catch {
    return {}
  }
}

async function parseError(response: Response): Promise<ApiError> {
  let detail = `${response.status} ${response.statusText}`
  try {
    const body = await response.json()
    if (typeof body?.detail === 'string') detail = body.detail
  } catch {
    // Non-JSON error body — keep the status text.
  }
  if (response.status === 401) {
    detail = 'Sign in to use this feature.'
  }
  return new ApiError(detail, response.status)
}

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, {
    signal,
    headers: await authHeaders(),
  })
  if (!response.ok) throw await parseError(response)
  return (await response.json()) as T
}

async function sendJson<T>(
  method: 'POST' | 'PUT' | 'DELETE',
  url: string,
  body: unknown,
  options: { signal?: AbortSignal } = {},
): Promise<T> {
  const response = await fetch(url, {
    method,
    signal: options.signal,
    headers: {
      'Content-Type': 'application/json',
      ...(await authHeaders()),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
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
  await sendJson(
    bookmarked ? 'PUT' : 'DELETE',
    `/api/papers/${encodeURIComponent(arxivId)}/bookmark${query}`,
    undefined,
  )
}

export async function markNotInterested(
  arxivId: string,
  context?: SearchContext,
): Promise<void> {
  const query = context
    ? `?searchEventId=${context.searchEventId}&rank=${context.rank}`
    : ''
  await sendJson(
    'POST',
    `/api/papers/${encodeURIComponent(arxivId)}/not-interested${query}`,
    undefined,
  )
}

export function getCategories(signal?: AbortSignal): Promise<CategoryDto[]> {
  return getJson('/api/categories', signal)
}

// --- Smart search ---

export function compileSearch(query: string, signal?: AbortSignal): Promise<SearchPlan> {
  return sendJson('POST', '/api/search/compile', { query }, { signal })
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
  return sendJson('POST', '/api/admin/analysis/selection', { arxivIds, searchEventId })
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

// --- Account ---

export function getMe(signal?: AbortSignal): Promise<MeView> {
  return getJson('/api/me', signal)
}

export function setThemePreference(theme: 'dark' | 'light'): Promise<void> {
  return sendJson('PUT', '/api/me/theme', { theme })
}

export function redeemInvite(code: string): Promise<void> {
  return sendJson('POST', '/api/me/invite', { code })
}

// --- Admin: accounts ---

export function getAdminUsers(query?: string, signal?: AbortSignal): Promise<AdminUserView[]> {
  const qs = query ? `?query=${encodeURIComponent(query)}` : ''
  return getJson(`/api/admin/users${qs}`, signal)
}

export function grantBudget(userId: number, amountMicros: number, note?: string): Promise<void> {
  return sendJson('POST', `/api/admin/users/${userId}/grants`, { amountMicros, note })
}

export function setUserRole(userId: number, role: 'Member' | 'Admin'): Promise<void> {
  return sendJson('PUT', `/api/admin/users/${userId}/role`, { role })
}

export function getInvites(signal?: AbortSignal): Promise<InviteView[]> {
  return getJson('/api/admin/invites', signal)
}

export function createInvite(maxUses: number, expiresDays?: number): Promise<InviteView> {
  return sendJson('POST', '/api/admin/invites', { maxUses, expiresDays })
}

// --- Settings ---

export function getLlmSettings(signal?: AbortSignal): Promise<LlmSettingsView> {
  return getJson('/api/admin/settings/llm', signal)
}

export function setLlmAssignment(step: string, modelId: string): Promise<void> {
  return sendJson('PUT', '/api/admin/settings/llm', { step, modelId })
}

export function getProfile(signal?: AbortSignal): Promise<ProfileView> {
  return getJson('/api/me/profile', signal)
}

export function saveProfile(
  experienceSummary: string,
  goals: string,
  weeklyHours: number | null,
): Promise<ProfileView> {
  return sendJson('PUT', '/api/me/profile', {
    experienceSummary,
    goals,
    weeklyHours,
  })
}
