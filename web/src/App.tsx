import { useState } from 'react'
import type { SortOrder } from './api/types'
import { CategoryFilter } from './components/CategoryFilter'
import { Pagination } from './components/Pagination'
import { PaperList } from './components/PaperList'
import { useCategories } from './hooks/useCategories'
import { usePapers } from './hooks/usePapers'
import './App.css'

const PAGE_SIZE = 25

export default function App() {
  const [selectedCategories, setSelectedCategories] = useState<string[]>([])
  const [page, setPage] = useState(1)
  const [sort, setSort] = useState<SortOrder>('published_desc')
  const [analyzedOnly, setAnalyzedOnly] = useState(false)

  const { categories, error: categoriesError } = useCategories()
  const { data, loading, error } = usePapers(
    selectedCategories,
    page,
    PAGE_SIZE,
    sort,
    analyzedOnly,
  )

  function handleCategoriesChange(next: string[]) {
    setSelectedCategories(next)
    setPage(1)
  }

  function handleSortChange(next: SortOrder) {
    setSort(next)
    setPage(1)
  }

  function handleAnalyzedOnlyChange(next: boolean) {
    setAnalyzedOnly(next)
    setPage(1)
  }

  return (
    <div className="app">
      <header className="app-header">
        <h1>Research Discovery</h1>
        <p>Browse recent arXiv papers and find your next project.</p>
      </header>
      <div className="app-body">
        <CategoryFilter
          categories={categories}
          selected={selectedCategories}
          onChange={handleCategoriesChange}
        />
        <main className="app-main">
          <div className="toolbar">
            <label>
              Sort by{' '}
              <select
                value={sort}
                onChange={(e) => handleSortChange(e.target.value as SortOrder)}
              >
                <option value="published_desc">Newest first</option>
                <option value="published_asc">Oldest first</option>
                <option value="score_desc">Best project score</option>
              </select>
            </label>
            <label className="toolbar-toggle">
              <input
                type="checkbox"
                checked={analyzedOnly}
                onChange={(e) => handleAnalyzedOnlyChange(e.target.checked)}
              />{' '}
              Analyzed only
            </label>
          </div>
          {categoriesError && (
            <p className="status status-error">Could not load categories: {categoriesError}</p>
          )}
          <PaperList data={data} loading={loading} error={error} />
          <Pagination
            page={page}
            totalPages={data?.totalPages ?? 0}
            totalItems={data?.totalItems ?? 0}
            onPageChange={setPage}
          />
        </main>
      </div>
    </div>
  )
}
