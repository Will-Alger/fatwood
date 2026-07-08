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
}

// Schema v1 of the LLM analysis document (AnalysisContract.SchemaJson).
export interface AnalysisDetails {
  summary: string
  feasibility_score: number
  feasibility_rationale: string
  estimated_effort: 'weekend' | 'one_to_two_weeks' | 'about_a_month' | 'multi_month'
  approach: 'reproduce' | 'extend'
  approach_rationale: string
  reference_code_likelihood: 'high' | 'medium' | 'low'
  resume_signal: string
  fintech_relevance_score: number
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
