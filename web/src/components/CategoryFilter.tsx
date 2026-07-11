import { useMemo, useState } from 'react'
import type { CategoryDto } from '../api/types'
import { categoryGloss } from '../data/categoryGloss'

interface CategoryFilterProps {
  categories: CategoryDto[]
  selected: string[]
  onChange: (selected: string[]) => void
}

/**
 * Searchable category panel: filter by code, official name, or the
 * plain-English gloss, sorted by paper count so the big categories are never
 * a scroll away. Collapses to a toggle on small screens (CSS).
 */
export function CategoryFilter({ categories, selected, onChange }: CategoryFilterProps) {
  const [filter, setFilter] = useState('')
  const [openOnMobile, setOpenOnMobile] = useState(false)

  const visible = useMemo(() => {
    const sorted = [...categories].sort((a, b) => b.paperCount - a.paperCount)
    const needle = filter.trim().toLowerCase()
    if (!needle) return sorted
    return sorted.filter(
      (c) =>
        c.code.toLowerCase().includes(needle) ||
        c.name.toLowerCase().includes(needle) ||
        categoryGloss(c.code).toLowerCase().includes(needle),
    )
  }, [categories, filter])

  function toggle(code: string) {
    onChange(
      selected.includes(code) ? selected.filter((c) => c !== code) : [...selected, code],
    )
  }

  return (
    <aside className={openOnMobile ? 'category-filter category-filter-open' : 'category-filter'}>
      <button
        type="button"
        className="category-filter-mobile-toggle"
        onClick={() => setOpenOnMobile(!openOnMobile)}
      >
        Categories{selected.length > 0 ? ` · ${selected.length} selected` : ''}
        <span aria-hidden="true">{openOnMobile ? ' ▴' : ' ▾'}</span>
      </button>

      <div className="category-filter-body">
        <div className="category-filter-header">
          <h2>Categories</h2>
          {selected.length > 0 && (
            <button type="button" className="link-button" onClick={() => onChange([])}>
              Clear ({selected.length})
            </button>
          )}
        </div>

        <input
          type="search"
          className="category-search"
          placeholder="Search categories…"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          aria-label="Search categories"
        />

        <ul>
          {visible.map((category) => (
            <li key={category.code}>
              <label title={categoryGloss(category.code)}>
                <input
                  type="checkbox"
                  checked={selected.includes(category.code)}
                  onChange={() => toggle(category.code)}
                />
                <span className="category-text">
                  <span className="category-line">
                    <span className="category-code">{category.code}</span>
                    <span className="category-count">
                      {category.paperCount.toLocaleString()}
                    </span>
                  </span>
                  <span className="category-name">{category.name}</span>
                  <span className="category-gloss">{categoryGloss(category.code)}</span>
                </span>
              </label>
            </li>
          ))}
          {visible.length === 0 && (
            <li className="category-empty">No categories match “{filter}”.</li>
          )}
        </ul>
      </div>
    </aside>
  )
}
