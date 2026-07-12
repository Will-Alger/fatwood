import { useEffect, useRef } from 'react'
import { isWaiting } from '../api/activity'

/**
 * The fuse: a hairline across the very top of the viewport that burns while
 * the app waits on the network. A bright ember head crawls left-to-right
 * consuming a faint unburned line ahead of it, leaving a glowing trail that
 * cools behind; when the last request settles, the head races to the right
 * edge and the whole thing fades — the familiar top-loading-bar pattern
 * (GitHub/YouTube) wearing the Fatwood skin.
 *
 * Activity comes from isWaiting() — fed by the API client around every
 * request — polled from the animation loop, so nothing re-renders.
 */

const BAR_HEIGHT = 8 // CSS px; the head glow needs a little headroom
const SWEEP_MS = 1400 // one full crawl at steady burn
const COMPLETE_MS = 260 // the finishing dash to the right edge
const COOL_MS = 550 // fade-out after completion

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
    faint: hexToRgb(style.getPropertyValue('--faint'), [110, 105, 95]),
    ember: hexToRgb(style.getPropertyValue('--ember'), [217, 122, 69]),
    core: hexToRgb(style.getPropertyValue('--ember-strong'), [232, 144, 88]),
  }
}

function rgba([r, g, b]: Rgb, alpha: number) {
  return `rgba(${r | 0}, ${g | 0}, ${b | 0}, ${alpha})`
}

type Phase = 'idle' | 'burning' | 'completing' | 'cooling'

export function FuseBar() {
  const canvasRef = useRef<HTMLCanvasElement | null>(null)

  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const ctx = canvas.getContext('2d')
    if (!ctx) return

    let palette = readPalette()
    let width = 0
    let phase: Phase = 'idle'
    let headX = 0
    let coolStart = 0
    let lastFrame = performance.now()
    let frame = 0

    const resize = () => {
      width = window.innerWidth
      const dpr = window.devicePixelRatio || 1
      canvas.width = Math.max(1, Math.round(width * dpr))
      canvas.height = Math.round(BAR_HEIGHT * dpr)
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
    }
    window.addEventListener('resize', resize)
    resize()

    const themeObserver = new MutationObserver(() => {
      palette = readPalette()
    })
    themeObserver.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ['data-theme'],
    })

    const step = (now: number) => {
      const dt = Math.min(80, now - lastFrame)
      lastFrame = now
      const waiting = isWaiting()

      // Phase transitions.
      if (phase === 'idle' && waiting) {
        phase = 'burning'
        headX = 0
      } else if (phase === 'burning' && !waiting) {
        phase = 'completing'
      } else if ((phase === 'completing' || phase === 'cooling') && waiting) {
        // New request mid-finish: resume the burn from where the head is.
        phase = 'burning'
        if (headX >= width) headX = 0
      }

      if (phase === 'idle') {
        frame = requestAnimationFrame(step)
        return
      }

      // The cooling trail: fade what's already drawn instead of clearing, so
      // the char behind the head dims smoothly over ~a second.
      ctx.globalCompositeOperation = 'destination-out'
      const fade = phase === 'cooling' ? dt / 140 : dt / 420
      ctx.fillStyle = `rgba(0, 0, 0, ${Math.min(1, fade)})`
      ctx.fillRect(0, 0, width, BAR_HEIGHT)
      ctx.globalCompositeOperation = 'source-over'

      if (phase === 'cooling') {
        if (now - coolStart > COOL_MS) {
          ctx.clearRect(0, 0, width, BAR_HEIGHT)
          phase = 'idle'
        }
        frame = requestAnimationFrame(step)
        return
      }

      // Advance the head: steady crawl while burning (wrapping for long
      // waits), a fast dash to the edge when the wait just ended.
      const speed = phase === 'completing' ? width / COMPLETE_MS : width / SWEEP_MS
      headX += speed * dt
      if (phase === 'burning' && headX >= width) headX = 0
      if (phase === 'completing' && headX >= width) {
        headX = width
        phase = 'cooling'
        coolStart = now
      }

      // Flush against the top edge — no gap above the line.
      const lineY = 1
      const flicker = 0.85 + 0.15 * Math.sin(now / 45)
      const boost = phase === 'completing' ? 1.4 : 1

      // The unburned fuse ahead: a faint hairline waiting to burn.
      if (headX < width - 4) {
        ctx.strokeStyle = rgba(palette.faint, 0.22)
        ctx.lineWidth = 1
        ctx.beginPath()
        ctx.moveTo(headX + 3, lineY)
        ctx.lineTo(width, lineY)
        ctx.stroke()
      }

      // The burn: a line brightening toward its leading tip, with a soft glow
      // on the stroke itself. The persistence fade above turns what's behind
      // it into the longer cooling trail.
      const tailLen = Math.min(160, headX)
      if (tailLen > 2) {
        const trail = ctx.createLinearGradient(headX - tailLen, 0, headX, 0)
        trail.addColorStop(0, rgba(palette.ember, 0))
        trail.addColorStop(0.75, rgba(palette.ember, 0.7 * flicker))
        trail.addColorStop(1, rgba(palette.core, 0.95 * flicker))
        ctx.shadowColor = rgba(palette.ember, 0.55)
        ctx.shadowBlur = 5 * boost
        ctx.strokeStyle = trail
        ctx.lineWidth = 2 * boost
        ctx.lineCap = 'butt'
        ctx.beginPath()
        ctx.moveTo(headX - tailLen, lineY)
        ctx.lineTo(headX, lineY)
        ctx.stroke()
        ctx.shadowBlur = 0
      }

      frame = requestAnimationFrame(step)
    }
    frame = requestAnimationFrame(step)

    return () => {
      cancelAnimationFrame(frame)
      window.removeEventListener('resize', resize)
      themeObserver.disconnect()
    }
  }, [])

  return <canvas ref={canvasRef} className="fuse-bar" aria-hidden="true" />
}
