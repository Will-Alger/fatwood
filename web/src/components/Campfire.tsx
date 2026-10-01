import { useEffect, useRef, type RefObject } from 'react'
import { startCampfire, type Spirit } from '../campfire/scene'

/**
 * The campfire on the Discover front page, and the fire spirit who lives in it.
 * Decorative: the canvas is hidden from assistive tech, and the scene stops
 * itself when it unmounts (results arrive) or scrolls out of view.
 *
 * `spiritRef` hands the page his handle so it can set his mood and point his
 * eyes at what you are doing. Hovering him warms him up; a poke makes him blink.
 */
export function Campfire({ spiritRef }: { spiritRef?: RefObject<Spirit | null> }) {
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const wrapRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const canvas = canvasRef.current
    const wrap = wrapRef.current
    if (!canvas || !wrap) return
    const spirit = startCampfire(canvas)
    if (spiritRef) spiritRef.current = spirit

    let over = false
    const onMove = (e: PointerEvent) => {
      const on = spirit.hit(e.clientX, e.clientY)
      wrap.style.cursor = on ? 'pointer' : ''
      if (on && !over && spirit.currentMood === 'idle') spirit.mood('happy', 1.2)
      over = on
    }
    const onClick = (e: MouseEvent) => {
      if (!spirit.hit(e.clientX, e.clientY)) return
      spirit.stoke(0.5)
      spirit.blink()
    }
    wrap.addEventListener('pointermove', onMove)
    wrap.addEventListener('click', onClick)
    return () => {
      wrap.removeEventListener('pointermove', onMove)
      wrap.removeEventListener('click', onClick)
      spirit.stop()
      if (spiritRef) spiritRef.current = null
    }
  }, [spiritRef])

  return (
    <div className="campfire" ref={wrapRef} aria-hidden="true">
      <canvas ref={canvasRef} />
    </div>
  )
}
