/** Matte placeholder cards shown wherever papers are being fetched. */
export function PaperSkeletons({ count = 4 }: { count?: number }) {
  return (
    <div className="paper-list" aria-hidden="true">
      {Array.from({ length: count }, (_, i) => (
        <div className="paper-card skeleton-card" key={i}>
          <div className="skeleton-line skeleton-title" />
          <div className="skeleton-line skeleton-meta" />
          <div className="skeleton-line" />
          <div className="skeleton-line" />
          <div className="skeleton-line skeleton-short" />
        </div>
      ))}
    </div>
  )
}

/** Three pulsing embers — the inline "working on it" indicator. */
export function EmberDots() {
  return (
    <span className="ember-dots" aria-hidden="true">
      <span />
      <span />
      <span />
    </span>
  )
}
