import { useEffect, useRef, useState } from 'react'

const TYPE_MS = 34
const DELETE_MS = 12
const HOLD_MS = 2200
const GAP_MS = 500

/**
 * Cycles through example texts with a typewriter effect, for use as an input
 * placeholder. Pauses while the user has typed anything (`active = false`).
 *
 * Deliberately does NOT honor prefers-reduced-motion: Windows commonly
 * reports it (the "show animations" toggle), which silently killed the effect
 * on desktop Chrome, and gently-changing placeholder text in an idle input is
 * not the vestibular-trigger class of motion the preference exists for.
 */
export function useTypingPlaceholder(examples: string[], active: boolean): string {
  const [text, setText] = useState('')
  const state = useRef({ example: 0, char: 0, deleting: false })

  useEffect(() => {
    if (!active || examples.length === 0) {
      return
    }

    let timer: number

    function tick() {
      const s = state.current
      const example = examples[s.example % examples.length]

      if (!s.deleting) {
        s.char++
        setText(example.slice(0, s.char))
        if (s.char >= example.length) {
          s.deleting = true
          timer = window.setTimeout(tick, HOLD_MS)
          return
        }
        timer = window.setTimeout(tick, TYPE_MS)
      } else {
        s.char--
        setText(example.slice(0, s.char))
        if (s.char <= 0) {
          s.deleting = false
          s.example++
          timer = window.setTimeout(tick, GAP_MS)
          return
        }
        timer = window.setTimeout(tick, DELETE_MS)
      }
    }

    timer = window.setTimeout(tick, GAP_MS)
    return () => window.clearTimeout(timer)
  }, [examples, active])

  if (!active) {
    return examples[0] ?? ''
  }

  return text
}
