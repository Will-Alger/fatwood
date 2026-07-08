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

export type SortOrder = 'published_desc' | 'published_asc'
