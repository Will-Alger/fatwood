import type { CategoryDto } from '../api/types'

interface CategoryFilterProps {
  categories: CategoryDto[]
  selected: string[]
  onChange: (selected: string[]) => void
}

export function CategoryFilter({ categories, selected, onChange }: CategoryFilterProps) {
  function toggle(code: string) {
    onChange(
      selected.includes(code)
        ? selected.filter((c) => c !== code)
        : [...selected, code],
    )
  }

  return (
    <aside className="category-filter">
      <div className="category-filter-header">
        <h2>Categories</h2>
        {selected.length > 0 && (
          <button type="button" className="link-button" onClick={() => onChange([])}>
            Clear
          </button>
        )}
      </div>
      <ul>
        {categories.map((category) => (
          <li key={category.code}>
            <label>
              <input
                type="checkbox"
                checked={selected.includes(category.code)}
                onChange={() => toggle(category.code)}
              />
              <span className="category-code">{category.code}</span>
              <span className="category-name">{category.name}</span>
              <span className="category-count">{category.paperCount.toLocaleString()}</span>
            </label>
          </li>
        ))}
      </ul>
    </aside>
  )
}
