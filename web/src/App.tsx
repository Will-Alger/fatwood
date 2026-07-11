import { useEffect, useState } from 'react'
import { getAdminKey, getLlmSettings } from './api/client'
import type { LlmSettingsView, SortOrder } from './api/types'
import { CategoryFilter } from './components/CategoryFilter'
import { Discover } from './components/Discover'
import { Logo } from './components/Logo'
import { Pagination } from './components/Pagination'
import { PaperList } from './components/PaperList'
import { SettingsPanel } from './components/SettingsPanel'
import { useCategories } from './hooks/useCategories'
import { usePapers } from './hooks/usePapers'
import './App.css'

const PAGE_SIZE = 25

type Tab = 'discover' | 'browse'
type Theme = 'dark' | 'light'

function currentTheme(): Theme {
  return document.documentElement.dataset.theme === 'light' ? 'light' : 'dark'
}

export default function App() {
  const [tab, setTab] = useState<Tab>('discover')
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [llmSettings, setLlmSettings] = useState<LlmSettingsView | null>(null)
  const [theme, setTheme] = useState<Theme>(currentTheme)

  const [selectedCategories, setSelectedCategories] = useState<string[]>([])
  const [page, setPage] = useState(1)
  const [sort, setSort] = useState<SortOrder>('published_desc')
  const [analyzedOnly, setAnalyzedOnly] = useState(false)
  const [bookmarkedOnly, setBookmarkedOnly] = useState(false)

  const { categories, error: categoriesError } = useCategories()
  const { data, loading, error } = usePapers(
    selectedCategories,
    page,
    PAGE_SIZE,
    sort,
    analyzedOnly,
    bookmarkedOnly,
  )

  useEffect(() => {
    if (getAdminKey() !== '') {
      getLlmSettings()
        .then(setLlmSettings)
        .catch(() => setLlmSettings(null))
    }
  }, [])

  function toggleTheme() {
    const next: Theme = theme === 'dark' ? 'light' : 'dark'
    document.documentElement.dataset.theme = next
    localStorage.setItem('fatwood.theme', next)
    setTheme(next)
  }

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
          <div className="app-brand">
            <Logo size={36} />
            <div>
              <h1>Fatwood</h1>
              <p>Kindling for your next build.</p>
            </div>
          </div>
          <div className="app-header-actions">
            <button
              type="button"
              className="icon-button"
              onClick={toggleTheme}
              title={theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}
              aria-label={theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}
            >
              {theme === 'dark' ? '☀' : '☾'}
            </button>
            <button
              type="button"
              className="icon-button"
              onClick={() => setSettingsOpen(true)}
              title="Settings"
              aria-label="Settings"
            >
              ⚙
            </button>
          </div>
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

      {/* Both panes stay mounted so switching tabs never loses state
          (search results, filters, scroll positions). */}
      <div style={{ display: tab === 'discover' ? undefined : 'none' }}>
        <Discover llmSettings={llmSettings} />
      </div>
      <div className="app-body" style={{ display: tab === 'browse' ? undefined : 'none' }}>
        <CategoryFilter
          categories={categories}
          selected={selectedCategories}
          onChange={handleCategoriesChange}
        />
        <main className="app-main">
          <div className="toolbar">
            <label>
              Sort by{' '}
              <select value={sort} onChange={(e) => handleSortChange(e.target.value as SortOrder)}>
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
            <label className="toolbar-toggle">
              <input
                type="checkbox"
                checked={bookmarkedOnly}
                onChange={(e) => {
                  setBookmarkedOnly(e.target.checked)
                  setPage(1)
                }}
              />{' '}
              ★ Bookmarked
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

      {settingsOpen && (
        <SettingsPanel onClose={() => setSettingsOpen(false)} onSettingsChanged={setLlmSettings} />
      )}
    </div>
  )
}
