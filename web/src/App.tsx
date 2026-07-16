import { useEffect, useState } from 'react'
import { getLlmSettings, redeemInvite, setThemePreference } from './api/client'
import type { LlmSettingsView, SortOrder } from './api/types'
import { AdminPanel } from './components/AdminPanel'
import { AuthPanel } from './components/AuthPanel'
import { CategoryFilter } from './components/CategoryFilter'
import { Discover } from './components/Discover'
import { FuseBar } from './components/FuseBar'
import { Logo } from './components/Logo'
import { Pagination } from './components/Pagination'
import { PaperList } from './components/PaperList'
import { SettingsPanel } from './components/SettingsPanel'
import { useAnalyze } from './hooks/useAnalyze'
import { useCategories } from './hooks/useCategories'
import { formatBudget, useMe } from './hooks/useMe'
import { usePapers } from './hooks/usePapers'
import './App.css'

const PAGE_SIZE = 25

type Tab = 'discover' | 'browse' | 'admin'
type Theme = 'dark' | 'light'

function currentTheme(): Theme {
  return document.documentElement.dataset.theme === 'light' ? 'light' : 'dark'
}

export default function App() {
  const [tab, setTab] = useState<Tab>('discover')
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [authOpen, setAuthOpen] = useState(false)
  const [llmSettings, setLlmSettings] = useState<LlmSettingsView | null>(null)
  const [theme, setTheme] = useState<Theme>(currentTheme)
  const [inviteCode, setInviteCode] = useState('')
  const [inviteError, setInviteError] = useState<string | null>(null)

  const [selectedCategories, setSelectedCategories] = useState<string[]>([])
  const [page, setPage] = useState(1)
  const [sort, setSort] = useState<SortOrder>('published_desc')
  const [analyzedOnly, setAnalyzedOnly] = useState(false)
  const [bookmarkedOnly, setBookmarkedOnly] = useState(false)

  const { me, ready, signedOut, refresh } = useMe()
  const { categories, error: categoriesError } = useCategories()
  const { data, loading, error, refetch } = usePapers(
    selectedCategories,
    page,
    PAGE_SIZE,
    sort,
    analyzedOnly,
    bookmarkedOnly,
  )
  // Analyzing a browsed paper refreshes the page in place so its card updates,
  // and the account so the budget chip reflects the spend.
  const { analyzingIds, analyzeOne } = useAnalyze(() => {
    refetch()
    refresh()
  })
  const [browseAnalyzeError, setBrowseAnalyzeError] = useState<string | null>(null)

  useEffect(() => {
    if (me?.role === 'Owner') {
      getLlmSettings()
        .then(setLlmSettings)
        .catch(() => setLlmSettings(null))
    }
  }, [me?.role])

  // The account's saved theme wins over the local default once known.
  useEffect(() => {
    if (me?.theme && me.theme !== currentTheme()) {
      document.documentElement.dataset.theme = me.theme
      localStorage.setItem('fatwood.theme', me.theme)
      setTheme(me.theme)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [me?.id])

  function toggleTheme() {
    const next: Theme = theme === 'dark' ? 'light' : 'dark'
    document.documentElement.dataset.theme = next
    localStorage.setItem('fatwood.theme', next)
    setTheme(next)
    if (me) {
      // Fire-and-forget; localStorage already has the fallback.
      setThemePreference(next).catch(() => undefined)
    }
  }

  async function handleInviteRedeem() {
    setInviteError(null)
    try {
      await redeemInvite(inviteCode)
      setInviteCode('')
      refresh()
    } catch (err) {
      setInviteError(err instanceof Error ? err.message : 'Could not redeem the code')
    }
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
      <FuseBar />
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
            {me && (
              <span
                className="budget-chip"
                title={
                  me.budget.unlimited
                    ? `${me.email} — unlimited (${me.role.toLowerCase()})`
                    : `${me.email} — remaining search & analysis budget`
                }
              >
                <span className="budget-chip-label">budget</span>
                {formatBudget(me)}
              </span>
            )}
            {ready && signedOut && (
              <button type="button" className="signin-button" onClick={() => setAuthOpen(true)}>
                Sign in
              </button>
            )}
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
          {me && me.role !== 'Member' && (
            <button
              type="button"
              className={tab === 'admin' ? 'tab tab-active' : 'tab'}
              onClick={() => setTab('admin')}
            >
              Admin
            </button>
          )}
        </nav>
      </header>

      {me && !me.isActive && (
        <div className="invite-banner">
          <span>
            Fatwood is invite-only right now. Enter an invite code to unlock search and
            analysis — browsing works without one.
          </span>
          <div className="invite-banner-form">
            <input
              value={inviteCode}
              onChange={(e) => setInviteCode(e.target.value)}
              placeholder="Invite code"
              aria-label="Invite code"
            />
            <button
              type="button"
              disabled={inviteCode.trim() === ''}
              onClick={() => void handleInviteRedeem()}
            >
              Unlock
            </button>
          </div>
          {inviteError && <p className="status status-error">{inviteError}</p>}
        </div>
      )}

      {/* Both panes stay mounted so switching tabs never loses state
          (search results, filters, scroll positions). */}
      <div style={{ display: tab === 'discover' ? undefined : 'none' }}>
        <Discover
          llmSettings={llmSettings}
          me={me}
          signedOut={signedOut}
          onSignIn={() => setAuthOpen(true)}
          refreshMe={refresh}
        />
      </div>
      {tab === 'admin' && me && me.role !== 'Member' && (
        <div className="app-body app-body-single">
          <AdminPanel me={me} />
        </div>
      )}

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
          {browseAnalyzeError && (
            <p className="status status-error">{browseAnalyzeError}</p>
          )}
          <PaperList
            data={data}
            loading={loading}
            error={error}
            canInteract={me?.isActive === true}
            analyzingIds={analyzingIds}
            onAnalyze={async (arxivId) => {
              setBrowseAnalyzeError(null)
              const err = await analyzeOne(arxivId)
              if (err) setBrowseAnalyzeError(err)
            }}
          />
          <Pagination
            page={page}
            totalPages={data?.totalPages ?? 0}
            totalItems={data?.totalItems ?? 0}
            onPageChange={setPage}
          />
        </main>
      </div>

      {authOpen && (
        <AuthPanel
          onClose={() => setAuthOpen(false)}
          onSignedIn={refresh}
        />
      )}

      {settingsOpen && (
        <SettingsPanel
          me={me}
          signedOut={signedOut}
          onClose={() => setSettingsOpen(false)}
          onSettingsChanged={setLlmSettings}
          onAccountChanged={refresh}
        />
      )}

      <footer className="app-footer">
        <span>© 2026 Fatwood</span>
        <a href="/terms.html">Terms</a>
        <a href="/privacy.html">Privacy</a>
        <span className="app-footer-note">
          Analyses are AI-generated — verify before you build. Paper data from arXiv
          (thank you to arXiv for use of its open access interoperability).
        </span>
      </footer>
    </div>
  )
}
