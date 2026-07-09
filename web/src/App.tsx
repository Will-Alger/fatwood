import { useEffect, useState } from 'react'
import { getAdminKey, getLlmSettings } from './api/client'
import type { LlmSettingsView, SortOrder } from './api/types'
import { CategoryFilter } from './components/CategoryFilter'
import { Discover } from './components/Discover'
import { Pagination } from './components/Pagination'
import { PaperList } from './components/PaperList'
import { SettingsPanel } from './components/SettingsPanel'
import { useCategories } from './hooks/useCategories'
import { usePapers } from './hooks/usePapers'
import './App.css'

const PAGE_SIZE = 25

type Tab = 'discover' | 'browse'

export default function App() {
  const [tab, setTab] = useState<Tab>('discover')
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [llmSettings, setLlmSettings] = useState<LlmSettingsView | null>(null)

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

  useEffect(() => {
    if (getAdminKey() !== '') {
      getLlmSettings()
        .then(setLlmSettings)
        .catch(() => setLlmSettings(null))
    }
  }, [])

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
        <div className="app-header-top">
          <div>
            <h1>Research Discovery</h1>
            <p>Find research you can actually build on.</p>
          </div>
          <button
            type="button"
            className="link-button"
            onClick={() => setSettingsOpen(true)}
          >
            ⚙ Settings
          </button>
        </div>
        <nav className="app-tabs">
          <button
            type="button"
            className={tab === 'discover' ? 'tab tab-active' : 'tab'}
            onClick={() => setTab('discover')}
          >
            Discover
          </button>
          <button
            type="button"
            className={tab === 'browse' ? 'tab tab-active' : 'tab'}
            onClick={() => setTab('browse')}
          >
            Browse
          </button>
        </nav>
      </header>

      {tab === 'discover' ? (
        <Discover llmSettings={llmSettings} />
      ) : (
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
      )}

      {settingsOpen && (
        <SettingsPanel
          onClose={() => setSettingsOpen(false)}
          onSettingsChanged={setLlmSettings}
        />
      )}
    </div>
  )
}
