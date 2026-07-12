interface PaginationProps {
  page: number
  totalPages: number
  totalItems: number
  onPageChange: (page: number) => void
  itemsLabel?: string
}

export function Pagination({
  page,
  totalPages,
  totalItems,
  onPageChange,
  itemsLabel = 'papers',
}: PaginationProps) {
  if (totalItems === 0) {
    return null
  }

  return (
    <nav className="pagination" aria-label="Pagination">
      <button type="button" disabled={page <= 1} onClick={() => onPageChange(page - 1)}>
        ← Previous
      </button>
      <span>
        Page {page} of {totalPages} ({totalItems.toLocaleString()} {itemsLabel})
      </span>
      <button
        type="button"
        disabled={page >= totalPages}
        onClick={() => onPageChange(page + 1)}
      >
        Next →
      </button>
    </nav>
  )
}
