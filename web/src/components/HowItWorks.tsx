import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, type CSSProperties } from 'react'
import { KIND_LABEL, LANE_OF, LANES, STEPS, type Lane } from '../data/architecture'

/**
 * The architecture, drawn as four flows: a search, an analysis, the nightly
 * ingest, and the platform around them. Every step is a card you can open;
 * each lane traces itself the first time it comes into view (and again on
 * request), drawing its path like a fuse.
 *
 * Connectors are measured from the laid-out cards (not hard-coded), so the
 * same markup reads left-to-right on a desktop and top-to-bottom on a phone.
 */
export function HowItWorks() {
  const [selected, setSelected] = useState<string | null>(null)

  const jumpTo = useCallback((id: string) => {
    setSelected(id)
    // Let the target lane render its panel, then bring the card into view.
    requestAnimationFrame(() => {
      document
        .getElementById(`flow-step-${id}`)
        ?.scrollIntoView({ behavior: 'smooth', block: 'center' })
    })
  }, [])

  // Esc closes whichever step is open.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setSelected(null)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  return (
    <div className="how">
      <header className="how-intro">
        <p className="hero-eyebrow">Under the hood</p>
        <h2>
          From a sentence to a <em>shortlist.</em>
        </h2>
        <p className="how-lede">
          Fatwood is one .NET app, one Postgres database and two search indexes held in memory.
          Open any step to see what it does and where it lives in the code.
        </p>
        <dl className="how-stats">
          <div>
            <dt>papers searched</dt>
            <dd>~925k</dd>
          </div>
          <div>
            <dt>LLM calls per search</dt>
            <dd>1</dd>
          </div>
          <div>
            <dt>hybrid vs. embeddings alone</dt>
            <dd>+17%</dd>
          </div>
          <div>
            <dt>nDCG@10 · CI floor 0.590</dt>
            <dd>0.628</dd>
          </div>
        </dl>
        <ul className="how-legend" aria-label="Legend">
          <li>
            <span className="flow-kind flow-kind-llm">{KIND_LABEL.llm}</span> costs money, logged to
            your budget
          </li>
          <li>
            <span className="flow-kind">{KIND_LABEL.local}</span> runs on our server, free
          </li>
        </ul>
      </header>

      {LANES.map((lane, i) => (
        <FlowLane
          key={lane.id}
          lane={lane}
          index={i}
          selected={selected && LANE_OF[selected] === lane.id ? selected : null}
          onSelect={setSelected}
          onJump={jumpTo}
        />
      ))}
    </div>
  )
}

const TRACE_MS = 620
const BEAT_GAP_MS = 140
const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches
const easeInOut = (t: number) => (t < 0.5 ? 4 * t * t * t : 1 - (-2 * t + 2) ** 3 / 2)

function FlowLane({
  lane,
  index,
  selected,
  onSelect,
  onJump,
}: {
  lane: Lane
  index: number
  selected: string | null
  onSelect: (id: string | null) => void
  onJump: (id: string) => void
}) {
  const laneRef = useRef<HTMLElement>(null)
  const gridRef = useRef<HTMLDivElement>(null)
  const panelRef = useRef<HTMLDivElement>(null)
  const cardRefs = useRef(new Map<string, HTMLButtonElement>())
  const fuseRefs = useRef<(SVGPathElement | null)[]>([])
  const dotRefs = useRef<(SVGCircleElement | null)[]>([])
  const rafRef = useRef(0)
  const [paths, setPaths] = useState<string[]>([])
  const [lit, setLit] = useState<Set<string>>(new Set())
  const [tracing, setTracing] = useState(false)
  const markerId = useId().replace(/:/g, '')
  const panelId = `flow-panel-${lane.id}`

  // Measure every card and draw a curve between each connected pair: out of
  // the right side into the left when the target is to the right, out of the
  // bottom into the top when it is below (the phone layout).
  useLayoutEffect(() => {
    const grid = gridRef.current
    if (!grid) return
    const measure = () => {
      const origin = grid.getBoundingClientRect()
      const box = (id: string) => {
        const r = cardRefs.current.get(id)!.getBoundingClientRect()
        return {
          l: r.left - origin.left,
          r: r.right - origin.left,
          t: r.top - origin.top,
          b: r.bottom - origin.top,
        }
      }
      setPaths(
        lane.edges.map(({ from, to }) => {
          const a = box(from)
          const b = box(to)
          const gap = 5
          if (b.l >= a.r - 1) {
            const x1 = a.r
            const y1 = (a.t + a.b) / 2
            const x2 = b.l - gap
            const y2 = (b.t + b.b) / 2
            const dx = (x2 - x1) / 2
            return `M${x1},${y1} C${x1 + dx},${y1} ${x2 - dx},${y2} ${x2},${y2}`
          }
          const x1 = (a.l + a.r) / 2
          const y1 = a.b
          const x2 = (b.l + b.r) / 2
          const y2 = b.t - gap
          const dy = (y2 - y1) / 2
          return `M${x1},${y1} C${x1},${y1 + dy} ${x2},${y2 - dy} ${x2},${y2}`
        }),
      )
    }
    measure()
    const ro = new ResizeObserver(measure)
    ro.observe(grid)
    return () => ro.disconnect()
  }, [lane])

  // The ember overlay on each connector is a dash as long as the path: fully
  // offset it is invisible, at zero it is drawn. Re-sync after every
  // re-measure so lit edges stay drawn at any width.
  const setFuse = useCallback((i: number, frac: number) => {
    const p = fuseRefs.current[i]
    if (!p) return
    const len = p.getTotalLength()
    p.style.strokeDasharray = `${len}`
    p.style.strokeDashoffset = `${len * (1 - frac)}`
  }, [])

  useLayoutEffect(() => {
    if (tracing) return
    lane.edges.forEach((_, i) => setFuse(i, lit.has(`e${i}`) ? 1 : 0))
  }, [paths, lit, tracing, lane, setFuse])

  useEffect(() => () => cancelAnimationFrame(rafRef.current), [])

  // Light the lane beat by beat: an ember runs along each edge of the beat,
  // drawing the fuse behind it, and the target catches when it arrives.
  const trace = useCallback(() => {
    cancelAnimationFrame(rafRef.current)
    if (reducedMotion()) {
      setLit(new Set([...lane.columns.flat(), ...lane.edges.map((_, i) => `e${i}`)]))
      return
    }
    lane.edges.forEach((_, i) => setFuse(i, 0))
    setLit(new Set(lane.trace[0].map((e) => lane.edges[e].from)))
    setTracing(true)
    let beat = 0
    let start = performance.now()
    const tick = (now: number) => {
      const edges = lane.trace[beat]
      const t = Math.max(0, Math.min(1, (now - start) / TRACE_MS))
      const e = easeInOut(t)
      for (const i of edges) {
        const path = fuseRefs.current[i]
        const dot = dotRefs.current[i]
        if (!path || !dot) continue
        const p = path.getPointAtLength(path.getTotalLength() * e)
        dot.setAttribute('cx', String(p.x))
        dot.setAttribute('cy', String(p.y))
        dot.style.opacity = t > 0 && t < 1 ? '1' : '0'
        setFuse(i, e)
      }
      if (t < 1) {
        rafRef.current = requestAnimationFrame(tick)
        return
      }
      setLit((prev) => {
        const next = new Set(prev)
        for (const i of edges) {
          next.add(`e${i}`)
          next.add(lane.edges[i].to)
        }
        return next
      })
      beat += 1
      if (beat >= lane.trace.length) {
        setTracing(false)
        return
      }
      start = now + BEAT_GAP_MS
      rafRef.current = requestAnimationFrame(tick)
    }
    rafRef.current = requestAnimationFrame(tick)
  }, [lane, setFuse])

  // Each lane traces itself once, the first time most of it is on screen
  // (not with reduced motion: then it waits for the button).
  useEffect(() => {
    const el = laneRef.current
    if (!el || reducedMotion()) return
    let timer = 0
    const io = new IntersectionObserver(
      ([entry]) => {
        if (!entry.isIntersecting) return
        io.disconnect()
        timer = window.setTimeout(trace, 250)
      },
      { threshold: 0.6 },
    )
    io.observe(el)
    return () => {
      io.disconnect()
      window.clearTimeout(timer)
    }
  }, [trace])

  // Opening a step brings its panel fully into view if it lands off screen.
  useEffect(() => {
    if (!selected) return
    const id = window.setTimeout(() => {
      panelRef.current?.scrollIntoView({
        behavior: reducedMotion() ? 'auto' : 'smooth',
        block: 'nearest',
      })
    }, 200)
    return () => window.clearTimeout(id)
  }, [selected])

  const order = lane.columns.flat()
  const step = selected ? STEPS[selected] : null
  const at = selected ? order.indexOf(selected) : -1

  return (
    <section
      className={tracing ? 'flow-lane flow-lane-tracing' : 'flow-lane'}
      ref={laneRef}
      aria-labelledby={`flow-lane-${lane.id}`}
    >
      <div className="flow-lane-head">
        <span className="flow-lane-number">{String(index + 1).padStart(2, '0')}</span>
        <div>
          <h3 id={`flow-lane-${lane.id}`}>{lane.title}</h3>
          <p>{lane.subtitle}</p>
        </div>
        <button type="button" className="flow-trace" onClick={trace} disabled={tracing}>
          <span className="flow-trace-dot" aria-hidden="true" />
          {lane.traceLabel}
        </button>
      </div>

      <div
        className="flow-grid"
        ref={gridRef}
        style={{ '--flow-cols': lane.columns.length } as CSSProperties}
      >
        <svg className="flow-edges" aria-hidden="true">
          <defs>
            <marker
              id={`${markerId}-a`}
              viewBox="0 0 8 8"
              refX="6"
              refY="4"
              markerWidth="8"
              markerHeight="8"
              orient="auto-start-reverse"
            >
              <path d="M1,1 L6,4 L1,7" className="flow-arrow" />
            </marker>
            <marker
              id={`${markerId}-b`}
              viewBox="0 0 8 8"
              refX="6"
              refY="4"
              markerWidth="8"
              markerHeight="8"
              orient="auto-start-reverse"
            >
              <path d="M1,1 L6,4 L1,7" className="flow-arrow flow-arrow-lit" />
            </marker>
          </defs>
          {paths.map((d, i) => (
            <path
              key={`base${i}`}
              d={d}
              className="flow-edge"
              markerEnd={`url(#${markerId}-${lit.has(`e${i}`) ? 'b' : 'a'})`}
            />
          ))}
          {paths.map((d, i) => (
            <path
              key={`fuse${i}`}
              ref={(el) => {
                fuseRefs.current[i] = el
              }}
              d={d}
              className="flow-fuse"
            />
          ))}
          {lane.edges.map((_, i) => (
            <circle
              key={i}
              ref={(el) => {
                dotRefs.current[i] = el
              }}
              r="4.5"
              className="flow-spark"
            />
          ))}
        </svg>

        {lane.columns.map((column, c) => (
          <div key={c} className={column.length > 1 ? 'flow-col flow-col-stack' : 'flow-col'}>
            {column.map((id) => {
              const s = STEPS[id]
              const isOpen = selected === id
              const classes = ['flow-step']
              if (isOpen) classes.push('flow-step-open')
              if (lit.has(id)) classes.push('flow-step-lit')
              return (
                <button
                  key={id}
                  id={`flow-step-${id}`}
                  type="button"
                  className={classes.join(' ')}
                  ref={(el) => {
                    if (el) cardRefs.current.set(id, el)
                    else cardRefs.current.delete(id)
                  }}
                  aria-expanded={isOpen}
                  aria-controls={panelId}
                  onClick={() => onSelect(isOpen ? null : id)}
                >
                  <span className={`flow-kind flow-kind-${s.kind}`}>{KIND_LABEL[s.kind]}</span>
                  <span className="flow-step-title">{s.title}</span>
                  <span className="flow-step-blurb">{s.blurb}</span>
                </button>
              )
            })}
          </div>
        ))}
      </div>

      {step && (
        <div className="flow-panel" id={panelId} ref={panelRef} role="region" aria-label={step.title}>
          {/* Keyed by step, so moving between steps fades the new one in. */}
          <div className="flow-panel-inner" key={step.id}>
            <div className="flow-panel-head">
              <span className={`flow-kind flow-kind-${step.kind}`}>{KIND_LABEL[step.kind]}</span>
              <h4>{step.title}</h4>
              <button
                type="button"
                className="flow-panel-close"
                aria-label="Close"
                onClick={() => onSelect(null)}
              >
                ×
              </button>
            </div>
            <div className="flow-panel-body">
              <div className="flow-panel-prose">
                {step.body.map((para, i) => (
                  <p key={i}>{para}</p>
                ))}
              </div>
              <div className="flow-panel-side">
                {step.facts && step.facts.length > 0 && (
                  <ul className="flow-facts">
                    {step.facts.map((f) => (
                      <li key={f}>{f}</li>
                    ))}
                  </ul>
                )}
                {step.code && step.code.length > 0 && (
                  <div className="flow-code">
                    <span className="flow-code-label">In the code</span>
                    {step.code.map((c) => (
                      <code key={c}>{c}</code>
                    ))}
                  </div>
                )}
              </div>
            </div>
            <div className="flow-panel-foot">
              <button
                type="button"
                className="flow-nav"
                disabled={at <= 0}
                onClick={() => onSelect(order[at - 1])}
              >
                ← {at > 0 ? STEPS[order[at - 1]].title : 'Previous'}
              </button>
              {step.link && (
                <button
                  type="button"
                  className="flow-nav flow-nav-jump"
                  onClick={() => onJump(step.link!.to)}
                >
                  {step.link.label} ↗
                </button>
              )}
              <button
                type="button"
                className="flow-nav"
                disabled={at >= order.length - 1}
                onClick={() => onSelect(order[at + 1])}
              >
                {at < order.length - 1 ? STEPS[order[at + 1]].title : 'Next'} →
              </button>
            </div>
          </div>
        </div>
      )}
    </section>
  )
}
