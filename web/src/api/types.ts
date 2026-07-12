// Mirrors the API DTOs in ResearchDiscovery.Application/Dtos/PaperDto.cs

export interface PaperDto {
  arxivId: string
  title: string
  abstract: string
  authors: string[]
  primaryCategory: string
  categories: string[]
  publishedUtc: string
  updatedUtc: string
  absUrl: string
  pdfUrl: string
  doi: string | null
  analysis: PaperAnalysisDto | null
  codeUrl: string | null
  isBookmarked: boolean
}

// Schema v2 of the LLM analysis document (AnalysisContract.SchemaJson):
// personalized paper × person evaluation.
export interface AnalysisDetails {
  summary: string
  feasibility_score: number
  hard_blockers: string[]
  learning_bridge: string
  estimated_effort: 'weekend' | 'one_to_two_weeks' | 'about_a_month' | 'multi_month'
  approach: 'reproduce' | 'extend'
  approach_rationale: string
  reference_code_likelihood: 'high' | 'medium' | 'low'
  goal_alignment_score: number
  resume_signal: string
  extension_idea: string
  required_skills: string[]
  composite_score: number
}

export interface PaperAnalysisDto {
  compositeScore: number | null
  model: string
  schemaVersion: number
  createdUtc: string
  details: AnalysisDetails
}

export interface CategoryDto {
  code: string
  name: string
  paperCount: number
}

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalItems: number
  totalPages: number
}

export type SortOrder = 'published_desc' | 'published_asc' | 'score_desc'

// --- Smart search ---

export interface SearchPlan {
  interpretation: string
  anchorText: string
  categories: string[]
  dateWindowDays: number | null
  requireNoCode: boolean | null
}

export interface SearchHit {
  paper: PaperDto
  matchScore: number
  isWildcard: boolean
  experienceProximity: 'close' | 'stretch' | null
}

export interface SearchResult {
  /** Telemetry id for this executed search; bookmark/analyze actions carry it as context. */
  searchEventId: number
  plan: SearchPlan
  hits: SearchHit[]
  totalCandidates: number
}

/** Ties an interaction (bookmark, analyze) back to the search that surfaced the paper. */
export interface SearchContext {
  searchEventId: number
  /** 1-based rank the paper held in the original result order. */
  rank: number
}

// --- Settings ---

export interface LlmModelView {
  id: string
  displayName: string
  inputPerMTok: number
  outputPerMTok: number
}

export interface LlmAssignmentView {
  step: string
  modelId: string
  isDefault: boolean
}

export interface LlmSettingsView {
  registry: LlmModelView[]
  assignments: LlmAssignmentView[]
  estAnalysisInputTokensPerPaper: number
  estAnalysisOutputTokensPerPaper: number
  estCompileInputTokens: number
  estCompileOutputTokens: number
}

export interface ProfileView {
  experienceSummary: string
  goals: string
  weeklyHours: number | null
  version: number
  updatedUtc: string | null
}

export interface BudgetView {
  grantedMicros: number
  spentMicros: number
  remainingMicros: number
  unlimited: boolean
}

export interface MeView {
  id: number
  email: string
  displayName: string
  role: 'Member' | 'Admin'
  isActive: boolean
  theme: 'dark' | 'light' | null
  budget: BudgetView
  byoKeyLast4: string | null
}

export interface AdminUserView {
  id: number
  email: string
  displayName: string
  role: 'Member' | 'Admin'
  isActive: boolean
  createdUtc: string
  lastSeenUtc: string
  grantedMicros: number
  spentMicros: number
}

export interface InviteView {
  id: number
  code: string
  maxUses: number
  usedCount: number
  expiresUtc: string | null
  createdUtc: string
}
