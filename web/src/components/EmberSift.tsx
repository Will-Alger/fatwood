import { useEffect, useRef, useState } from 'react'

/**
 * The ranking visual: a bed of dim specks (the corpus) drifts while random
 * ones catch fire, burn for a few seconds, and cool back to ash — the ranker
 * testing candidates. Shown only while the first search is in flight.
 *
 * Deliberately NOT gated on prefers-reduced-motion: Windows reports it when
 * "show animations" is off, which silently killed the typing effect on
 * desktop Chrome. The whole component fades in after a short delay instead,
 * so sub-second searches barely show it.
 */

const STAGES = [
  'Sifting tens of thousands of papers…',
  'Matching your exact terms…',
  'Scoring the survivors by meaning…',
  'Picking two wildcards from outside your lane…',
]
const STAGE_MS = 1700

const CANVAS_HEIGHT = 150
const IGNITE_EVERY_MS = 260
const FLARE_PORTION = 0.16 // first slice of the burn: quick bright flare-up
const FADE_PORTION = 0.3 // last slice: cool back down to ash

interface Speck {
  x: number
  y: number
  vx: number
  vy: number
  r: number
  alpha: number
  twinkle: number
  depth: number // 0.45 far … 1.2 near: scales size, brightness, and motion
  burnStart: number // 0 = drifting ash
  burnDur: number
}

type Rgb = [number, number, number]

function hexToRgb(hex: string, fallback: Rgb): Rgb {
  const h = hex.trim().replace('#', '')
  if (!/^[0-9a-fA-F]{3}$|^[0-9a-fA-F]{6}$/.test(h)) return fallback
  const full = h.length === 3 ? [...h].map((c) => c + c).join('') : h
  const n = parseInt(full, 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}

/** Theme palette straight from the CSS variables, so light/dark just work. */
function readPalette() {
  const style = getComputedStyle(document.documentElement)
  return {
    speck: hexToRgb(style.getPropertyValue('--faint'), [110, 105, 95]),
    ember: hexToRgb(style.getPropertyValue('--ember'), [217, 122, 69]),
    core: hexToRgb(style.getPropertyValue('--ember-strong'), [232, 144, 88]),
  }
}

function lerp(a: number, b: number, t: number) {
  return a + (b - a) * t
}

function lerpRgb(a: Rgb, b: Rgb, t: number): Rgb {
  return [lerp(a[0], b[0], t), lerp(a[1], b[1], t), lerp(a[2], b[2], t)]
}

function rgba([r, g, b]: Rgb, alpha: number) {
  return `rgba(${r | 0}, ${g | 0}, ${b | 0}, ${alpha})`
}

/** 0→1 over the flare, ~1 through the burn, →0 over the fade. */
function burnEnvelope(t: number) {
  if (t < FLARE_PORTION) return 1 - Math.pow(1 - t / FLARE_PORTION, 3)
  if (t > 1 - FADE_PORTION) return Math.max(0, (1 - t) / FADE_PORTION)
  return 1
}

function makeDrifter(width: number, height: number): Speck {
  const depth = 0.45 + Math.random() * 0.75
  return {
    x: Math.random() * width,
    y: 10 + Math.random() * (height - 20),
    vx: (Math.random() - 0.5) * 9 * depth,
    vy: (1.5 + Math.random() * 4) * depth,
    r: (1 + Math.random() * 1.1) * depth,
    alpha: (0.25 + Math.random() * 0.3) * Math.min(1, depth + 0.25),
    twinkle: Math.random() * Math.PI * 2,
    depth,
    burnStart: 0,
    burnDur: 0,
  }
}

export function EmberSift({ finishing = false }: { finishing?: boolean }) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null)
  const finishingRef = useRef(finishing)
  finishingRef.current = finishing
  const [stage, setStage] = useState(0)

  useEffect(() => {
    const id = window.setInterval(
      () => setStage((s) => Math.min(s + 1, STAGES.length - 1)),
      STAGE_MS,
    )
    return () => window.clearInterval(id)
  }, [])

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const ctx = canvas.getContext('2d')
    if (!ctx) return

    let palette = readPalette()
    let width = 0
    let specks: Speck[] = []
    const t0 = performance.now()
    let lastIgnite = t0
    let lastFrame = t0
    let finishStart = 0
    let frame = 0

    const resize = () => {
      width = canvas.clientWidth
      const dpr = window.devicePixelRatio || 1
      canvas.width = Math.max(1, Math.round(width * dpr))
      canvas.height = Math.round(CANVAS_HEIGHT * dpr)
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
      const target = Math.max(40, Math.min(90, Math.floor(width / 9)))
      while (specks.length < target) specks.push(makeDrifter(width, CANVAS_HEIGHT))
      if (specks.length > target) specks.length = target
    }

    const resizeObserver = new ResizeObserver(resize)
    resizeObserver.observe(canvas)
    resize()

    const themeObserver = new MutationObserver(() => {
      palette = readPalette()
    })
    themeObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-theme'],
    })

    const ignite = (now: number) => {
      // Activity ramps with the funnel copy: calm while "sifting", full
      // blaze by "picking wildcards".
      const ramp = Math.min(1, (now - t0) / (STAGE_MS * 4))
      const maxBurning = Math.round(lerp(2, Math.max(4, Math.floor(width / 110)), ramp))
      const burning = specks.reduce((n, s) => n + (s.burnStart > 0 ? 1 : 0), 0)
      if (burning >= maxBurning) return
      // Only near-layer specks catch: embers belong in the foreground.
      const candidates = specks.filter((s) => s.burnStart === 0 && s.depth > 0.75)
      if (candidates.length === 0) return
      const pick = candidates[Math.floor(Math.random() * candidates.length)]
      pick.burnStart = now
      pick.burnDur = 1600 + Math.random() * 1900
      // Catching fire gives it a pop: a sideways kick and a lift.
      pick.vx += (Math.random() - 0.5) * 90
      pick.vy -= 16 + Math.random() * 30
    }

    const step = (now: number) => {
      const dt = Math.min(0.05, (now - lastFrame) / 1000)
      lastFrame = now

      // Results just landed: every burning ember flares and lifts, and a
      // last handful catch — the panel fades out over it (CSS).
      if (finishingRef.current && finishStart === 0) {
        finishStart = now
        for (const s of specks) {
          if (s.burnStart > 0) s.vy -= 30
        }
        const fresh = specks.filter((s) => s.burnStart === 0 && s.depth > 0.75)
        for (let i = 0; i < 5 && fresh.length > 0; i++) {
          const [p] = fresh.splice(Math.floor(Math.random() * fresh.length), 1)
          p.burnStart = now
          p.burnDur = 900
          p.vy -= 20
        }
      }
      if (finishStart === 0 && now - lastIgnite > IGNITE_EVERY_MS) {
        lastIgnite = now
        ignite(now)
      }
      const flare = finishStart > 0 ? 1 + 1.4 * Math.max(0, 1 - (now - finishStart) / 350) : 1

      ctx.clearRect(0, 0, width, CANVAS_HEIGHT)

      for (const s of specks) {
        s.twinkle += dt * 2.2
        const t = s.burnStart > 0 ? (now - s.burnStart) / s.burnDur : 0
        if (t >= 1) {
          s.burnStart = 0
          s.vx = (Math.random() - 0.5) * 9
          s.vy = 1.5 + Math.random() * 4
        }
        const burn = s.burnStart > 0 ? burnEnvelope(t) : 0

        // Gusty wind everything rides — hot embers feel it far more than ash;
        // per-speck phase keeps them from moving in lockstep. Ash sinks; a
        // burning ember rides its own heat upward.
        const wind =
          Math.sin(now / 1800 + s.twinkle) * 13 + Math.sin(now / 520 + s.twinkle * 3) * 7
        const px = s.x
        const py = s.y
        s.x += (s.vx + wind * (0.25 + burn * 2.2) * s.depth) * dt
        s.y += (s.vy - burn * 18) * dt
        if (burn > 0) {
          // Turbulent hot air: jitter the velocity, with drag to keep it bounded.
          s.vx += (Math.random() - 0.5) * 150 * burn * dt
          s.vy += (Math.random() - 0.5) * 100 * burn * dt
          s.vx -= s.vx * 1.1 * dt
          s.vy -= s.vy * 0.6 * dt
        } else {
          s.vx += (Math.random() - 0.5) * 6 * dt
        }
        if (s.x < -4) s.x = width + 4
        if (s.x > width + 4) s.x = -4
        if (s.y > CANVAS_HEIGHT + 4) Object.assign(s, makeDrifter(width, CANVAS_HEIGHT), { y: 6 })
        if (s.y < -4) Object.assign(s, makeDrifter(width, CANVAS_HEIGHT), { y: CANVAS_HEIGHT - 6 })

        if (burn === 0) {
          const a = s.alpha * (0.65 + 0.35 * Math.sin(s.twinkle))
          ctx.fillStyle = rgba(palette.speck, a)
          ctx.beginPath()
          ctx.arc(s.x, s.y, s.r, 0, Math.PI * 2)
          ctx.fill()
        } else {
          const flicker = 0.85 + 0.15 * Math.sin(now / 90 + s.twinkle * 7)
          const radius = (s.r + burn * 2.4 * flicker) * flare
          // A short comet tail opposite the direction of travel — velocity is
          // invisible on a small dot without one. Skip on edge wrap (huge dx).
          const dx = s.x - px
          const dy = s.y - py
          if (dt > 0 && Math.abs(dx) < 50 && Math.abs(dy) < 50) {
            const tailX = s.x - (dx / dt) * 0.12
            const tailY = s.y - (dy / dt) * 0.12
            const tail = ctx.createLinearGradient(tailX, tailY, s.x, s.y)
            tail.addColorStop(0, rgba(palette.ember, 0))
            tail.addColorStop(1, rgba(palette.ember, 0.5 * burn))
            ctx.strokeStyle = tail
            ctx.lineWidth = radius * 1.1
            ctx.lineCap = 'round'
            ctx.beginPath()
            ctx.moveTo(tailX, tailY)
            ctx.lineTo(s.x, s.y)
            ctx.stroke()
          }
          ctx.shadowColor = rgba(palette.ember, 0.75 * burn)
          ctx.shadowBlur = 14 * burn * flare
          ctx.fillStyle = rgba(lerpRgb(palette.speck, palette.ember, burn), lerp(s.alpha, 1, burn))
          ctx.beginPath()
          ctx.arc(s.x, s.y, radius, 0, Math.PI * 2)
          ctx.fill()
          ctx.shadowBlur = 0
          if (burn > 0.4) {
            ctx.fillStyle = rgba(palette.core, (burn - 0.4) * 1.6 * flicker)
            ctx.beginPath()
            ctx.arc(s.x, s.y, radius * 0.45, 0, Math.PI * 2)
            ctx.fill()
          }
        }
      }

      frame = requestAnimationFrame(step)
    }
    frame = requestAnimationFrame(step)

    return () => {
      cancelAnimationFrame(frame)
      resizeObserver.disconnect()
      themeObserver.disconnect()
    }
  }, [])

  return (
    <div className={finishing ? 'ember-sift ember-sift-finishing' : 'ember-sift'} role="status">
      <canvas
        ref={canvasRef}
        className="ember-sift-canvas"
        height={CANVAS_HEIGHT}
        aria-hidden="true"
      />
      <p className="ember-sift-stage" key={stage}>
        {STAGES[stage]}
      </p>
    </div>
  )
}
