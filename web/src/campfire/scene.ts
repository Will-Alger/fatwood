/* The campfire on the Discover front page, drawn straight onto the page: no
   sky, no clearing, no smoke, a transparent canvas over the paper. Charcoal
   logs on a soft scorch, the traced hand-drawn flame, embers, and a small fire
   spirit living in the flame's core.

   Ported from prototypes/campfire-hero.html. The shell sizes to its canvas,
   pauses off screen and in hidden tabs, and returns a handle the page uses to
   set his mood and point his eyes (see Spirit below).

   Reduced motion does not stop the fire, deliberately: Windows reports reduced
   motion whenever "Animation effects" is off, which would leave most desktop
   visitors with a still fire (the same trap noted in App.css). */

import { CORE_FRAMES, FLAME_FRAMES } from './frames'

export type Mood = 'idle' | 'curious' | 'focused' | 'happy' | 'delighted' | 'sleepy'

export interface Spirit {
  /** Make the fire flare for a moment (0..1, adds up, decays). */
  stoke(amount: number): void
  /** Set his mood, optionally for `secs`, then fall back to `then`. */
  mood(name: Mood, secs?: number, then?: Mood): void
  /** Look at a point on the page (client coordinates) for a while. */
  look(x: number, y: number, secs?: number): void
  /** A quick double blink. */
  blink(): void
  /** Is this client point on the fire? */
  hit(x: number, y: number): boolean
  readonly currentMood: Mood
  stop(): void
}

type Pt = [number, number]

export function startCampfire(canvas: HTMLCanvasElement): Spirit {
  const ctx = canvas.getContext('2d')!
  const FIRE_X = 500
  // The part of the 1000x750 design the camera holds: the fire, and room above
  // it for embers. Bottom-anchored, so the logs always sit on the hero's floor.
  const VIEW = { x: 300, y: 330, w: 400, h: 425 }
  const PAPER: number[] = [240, 236, 226]
  const INK: number[] = [32, 29, 25]
  // The traced drawings originally played at 12 a second; slower reads calmer.
  const FLAME_FPS = 9

  let seed = 7
  const rnd = () => ((seed = (seed * 16807) % 2147483647) - 1) / 2147483646
  const R = (a: number, b: number) => a + rnd() * (b - a)
  const wob = (t: number, s: number) =>
    Math.sin(t * 1.7 + s) * 0.5 + Math.sin(t * 3.1 + s * 2.3) * 0.3 + Math.sin(t * 7.3 + s * 0.7) * 0.2
  const rgba = (c: number[], a: number) => `rgba(${c[0]},${c[1]},${c[2]},${a})`
  const hash = (i: number, k: number) => {
    const x = Math.sin(i * 127.1 + k * 311.7) * 43758.5453
    return x - Math.floor(x)
  }

  let VT = 0, VB = 0, VL = 0, VR = 0, dpr = 1, scale = 1, ox = 0, oy = 0
  let staticLayer!: HTMLCanvasElement, fireLayer!: HTMLCanvasElement
  let faceLayer!: HTMLCanvasElement, frontLogLayer!: HTMLCanvasElement
  let speckPattern!: CanvasPattern

  const mkCanvas = (w: number, h: number) => {
    const c = document.createElement('canvas')
    c.width = w
    c.height = h
    return c
  }
  const toDesign = (g: CanvasRenderingContext2D) =>
    g.setTransform(dpr * scale, 0, 0, dpr * scale, dpr * ox, dpr * oy)

  // ---------- static scene: the ash bed and the back logs ----------
  function buildStatic() {
    seed = 7
    const g = staticLayer.getContext('2d')!
    toDesign(g)
    // a soft scorch on the paper where the fire sits, so the pile has weight
    g.save()
    g.translate(FIRE_X, 712)
    g.scale(1, 0.16)
    const sh = g.createRadialGradient(0, 0, 0, 0, 0, 200)
    sh.addColorStop(0, rgba(INK, 0.28))
    sh.addColorStop(0.55, rgba(INK, 0.1))
    sh.addColorStop(1, rgba(INK, 0))
    g.fillStyle = sh
    g.beginPath()
    g.arc(0, 0, 200, 0, 7)
    g.fill()
    g.restore()
    // ash and charcoal crumbs round the base, thinning outward
    for (let i = 0; i < 70; i++) {
      const a = R(0, Math.PI * 2), d = Math.pow(rnd(), 0.7) * 175
      const x = FIRE_X + Math.cos(a) * d, y = 712 + Math.sin(a) * d * 0.14
      const s = R(0.8, 3.4) * (1 - d / 260)
      g.fillStyle = rgba(INK, R(0.35, 0.85))
      g.beginPath()
      g.ellipse(x, y, s, s * 0.7, R(0, 3), 0, 7)
      g.fill()
    }
    drawBackLogs(g)
    sprinkle(g)
  }

  // texture: paper and soot specks, clipped to what's painted
  function sprinkle(g: CanvasRenderingContext2D) {
    g.globalCompositeOperation = 'source-atop'
    const area = ((VR - VL) * (VB - VT)) / (1000 * 750)
    for (let i = 0, n = 14000 * area; i < n; i++) {
      g.fillStyle = rgba(PAPER, R(0.05, 0.2))
      const s = R(0.5, 1.6)
      g.fillRect(R(VL, VR), R(VT, VB), s, s)
    }
    for (let i = 0, n = 9000 * area; i < n; i++) {
      g.fillStyle = rgba([8, 7, 6], R(0.15, 0.4))
      const s = R(0.6, 1.8)
      g.fillRect(R(VL, VR), R(VT, VB), s, s)
    }
    g.globalCompositeOperation = 'source-over'
  }

  function buildSpeck() {
    const c = mkCanvas(256, 256), g = c.getContext('2d')!
    for (let i = 0; i < 4200; i++) {
      g.fillStyle = `rgba(0,0,0,${R(0.3, 1)})`
      const s = R(0.6, 1.8)
      g.fillRect(R(0, 256), R(0, 256), s, s)
    }
    return c
  }

  function resize(): boolean {
    const r = canvas.getBoundingClientRect()
    if (!r.width || !r.height) return false
    dpr = Math.min(window.devicePixelRatio || 1, 2)
    canvas.width = Math.round(r.width * dpr)
    canvas.height = Math.round(r.height * dpr)
    scale = Math.min(r.width / VIEW.w, r.height / VIEW.h)
    ox = (r.width - VIEW.w * scale) / 2 - VIEW.x * scale
    oy = r.height - (VIEW.y + VIEW.h) * scale
    VL = -ox / scale
    VR = (r.width - ox) / scale
    VT = -oy / scale
    VB = (r.height - oy) / scale
    staticLayer = mkCanvas(canvas.width, canvas.height)
    fireLayer = mkCanvas(canvas.width, canvas.height)
    faceLayer = mkCanvas(canvas.width, canvas.height)
    frontLogLayer = mkCanvas(canvas.width, canvas.height)
    buildStatic()
    const g = frontLogLayer.getContext('2d')!
    toDesign(g)
    drawFrontLogs(g)
    sprinkle(g)
    speckPattern = fireLayer.getContext('2d')!.createPattern(buildSpeck(), 'repeat')!
    return true
  }

  // ---------- logs, inked in charcoal ----------
  interface LogOpts { lines?: number[][]; grain?: string; cap?: boolean }
  function log(g: CanvasRenderingContext2D, x1: number, y1: number, x2: number, y2: number,
    th: number, top: string, bot: string, opts: LogOpts = {}) {
    const L = Math.hypot(x2 - x1, y2 - y1), a = Math.atan2(y2 - y1, x2 - x1), r = th / 2
    g.save()
    g.translate(x1, y1)
    g.rotate(a)
    const lg = g.createLinearGradient(0, -r, 0, r)
    lg.addColorStop(0, top)
    lg.addColorStop(1, bot)
    g.fillStyle = lg
    g.beginPath()
    g.roundRect(0, -r, L, th, r)
    g.fill()
    g.strokeStyle = opts.grain ?? rgba(PAPER, 0.32)
    g.lineWidth = 1.8
    g.lineCap = 'round'
    for (const [v, s0, s1] of opts.lines ?? []) {
      g.beginPath()
      for (let k = 0; k <= 16; k++) {
        const s = s0 + ((s1 - s0) * k) / 16, x = s * L, y = v * r * 0.7 + Math.sin(s * 9 + v * 5) * 2.2
        if (k) g.lineTo(x, y)
        else g.moveTo(x, y)
      }
      g.stroke()
    }
    if (opts.cap) {   // cut end: pale heartwood with a growth ring
      g.fillStyle = '#cbc3b3'
      g.beginPath()
      g.ellipse(L - r * 0.55, 0, r * 0.62, r * 0.98, 0, 0, 7)
      g.fill()
      g.strokeStyle = '#6f665a'
      g.lineWidth = 2.4
      g.beginPath()
      g.ellipse(L - r * 0.55, 1, r * 0.3, r * 0.5, 0, 0, 7)
      g.stroke()
    }
    g.restore()
  }
  function drawBackLogs(g: CanvasRenderingContext2D) {
    log(g, 414, 662, 500, 684, 30, '#3d3832', '#24211d', { lines: [[-0.2, 0.2, 0.8]] })
    log(g, 498, 690, 596, 650, 34, '#3d3832', '#24211d', { lines: [[-0.3, 0.35, 0.85], [0.25, 0.5, 0.9]] })
  }
  function drawFrontLogs(g: CanvasRenderingContext2D) {
    log(g, 392, 712, 520, 702, 36, '#776f65', '#5c564d', { lines: [[0.1, 0.12, 0.6]], grain: rgba(PAPER, 0.4) })
    log(g, 398, 684, 612, 706, 40, '#433b34', '#1d1a17', {
      cap: true,
      lines: [[-0.35, 0.12, 0.45], [-0.1, 0.5, 0.85], [0.4, 0.3, 0.7]],
    })
  }

  // ---------- fire: traced keyframes ----------
  // closed Catmull-Rom spline through the traced points, so it reads as a drawn curve
  function smoothPath(pts: number[]) {
    const P = new Path2D(), n = pts.length / 2
    const X = (i: number) => pts[((i + n) % n) * 2], Y = (i: number) => pts[((i + n) % n) * 2 + 1]
    P.moveTo(X(0), Y(0))
    for (let i = 0; i < n; i++) {
      P.bezierCurveTo(
        X(i) + (X(i + 1) - X(i - 1)) / 6, Y(i) + (Y(i + 1) - Y(i - 1)) / 6,
        X(i + 1) - (X(i + 2) - X(i)) / 6, Y(i + 1) - (Y(i + 2) - Y(i)) / 6,
        X(i + 1), Y(i + 1),
      )
    }
    P.closePath()
    return P
  }
  // The lower body stays one steady round bulb while only the top animates: ease
  // every point below BULB.top onto a fixed oval.
  const BULB = { x: -2, y: -60, rx: 68, ry: 66, top: -125 }
  function settleBase(f: number[]) {
    const out = f.slice()
    for (let i = 0; i < f.length; i += 2) {
      const x = f[i], y = f[i + 1]
      const u = Math.min(1, Math.max(0, (y - BULB.top) / (BULB.y - BULB.top))), w = u * u * (3 - 2 * u)
      if (!w) continue
      const dx = x - BULB.x, dy = y - BULB.y
      let tx: number, ty: number
      if (dy < 0) {
        tx = BULB.x + Math.sign(dx || 1) * BULB.rx * Math.sqrt(Math.max(0, 1 - (dy / BULB.ry) ** 2))
        ty = y
      } else {
        const d = Math.hypot(dx / BULB.rx, dy / BULB.ry) || 1
        tx = BULB.x + dx / d
        ty = BULB.y + dy / d
      }
      out[i] = x + (tx - x) * w
      out[i + 1] = y + (ty - y) * w
    }
    return out
  }
  const FLAME_PATHS = FLAME_FRAMES.map((f) => smoothPath(settleBase(f)))
  const CORE_PATHS = CORE_FRAMES.map((f) => smoothPath(f))
  // topmost point of each outline, where the breakaway licks leave from
  const FLAME_TIPS: Pt[] = FLAME_FRAMES.map((f) => {
    let k = 1
    for (let i = 3; i < f.length; i += 2) if (f[i] < f[k]) k = i
    return [f[k - 1], f[k]]
  })
  function drop(g: CanvasRenderingContext2D, x: number, y: number, s: number) {
    g.moveTo(x, y - s * 1.7)
    g.bezierCurveTo(x + s * 1.05, y - s * 0.5, x + s * 0.85, y + s, x, y + s)
    g.bezierCurveTo(x - s * 0.85, y + s, x - s * 1.05, y - s * 0.5, x, y - s * 1.7)
  }

  const FLAME_X = FIRE_X, FLAME_Y = 698   // base sinks behind the front logs
  const licks: { x: number; y: number; s: number; dx: number; age: number }[] = []
  let lastFi = -1
  function drawFire(fi: number, flick: number, stoke: number) {
    const k = fi % FLAME_PATHS.length
    const sx = 1 + 0.04 * stoke, sy = 1 + 0.14 * stoke   // a stoked fire stands taller
    const path = FLAME_PATHS[k], tip = FLAME_TIPS[k]
    if (fi !== lastFi) {
      lastFi = fi
      for (let i = licks.length - 1; i >= 0; i--) {
        const l = licks[i]
        l.y -= 15
        l.x += l.dx
        l.s *= 0.75
        if (++l.age > 4) licks.splice(i, 1)
      }
      if (hash(fi, 11) < 0.35 + 0.4 * stoke) {
        licks.push({
          x: FLAME_X + tip[0] * sx + (hash(fi, 13) * 2 - 1) * 5, y: FLAME_Y + tip[1] * sy - 16,
          s: 5 + hash(fi, 14) * 4, dx: (hash(fi, 15) * 2 - 1) * 3, age: 0,
        })
      }
    }
    const g = fireLayer.getContext('2d')!
    g.setTransform(1, 0, 0, 1, 0, 0)
    g.clearRect(0, 0, fireLayer.width, fireLayer.height)
    toDesign(g)
    g.save()
    g.translate(FLAME_X, FLAME_Y)
    g.scale(sx, sy)
    const fg = g.createLinearGradient(0, 0, 0, -200)
    fg.addColorStop(0, '#f2702e')
    fg.addColorStop(0.3, '#f98a36')
    fg.addColorStop(0.55, '#f5702b')
    fg.addColorStop(1, '#df4a22')
    g.fillStyle = fg
    g.fill(path)
    // soft-edged core: a same-colour shadow so it glows into the orange
    g.save()
    g.clip(path)
    g.fillStyle = '#fcd03a'
    g.shadowColor = '#fcd03a'
    g.shadowBlur = 10 * dpr * scale
    g.fill(CORE_PATHS[k])
    g.restore()
    // the bulb over the logs: deepen toward red, then fade so the logs read through
    g.globalCompositeOperation = 'source-atop'
    const tint = g.createLinearGradient(0, BULB.y - 10, 0, BULB.y + BULB.ry)
    tint.addColorStop(0, 'rgba(140,28,24,0)')
    tint.addColorStop(1, 'rgba(140,28,24,.5)')
    g.fillStyle = tint
    g.fillRect(-120, BULB.y - 10, 240, 120)
    g.globalCompositeOperation = 'destination-in'
    const fade = g.createLinearGradient(0, BULB.y - 20, 0, BULB.y + BULB.ry)
    fade.addColorStop(0, 'rgba(0,0,0,1)')
    fade.addColorStop(0.55, 'rgba(0,0,0,.72)')
    fade.addColorStop(1, 'rgba(0,0,0,.5)')
    g.fillStyle = fade
    g.fillRect(-300, -400, 600, 500)
    // heavier speckle in the bulb, so it dissolves into grain over the logs
    g.globalCompositeOperation = 'destination-out'
    g.save()
    g.beginPath()
    g.rect(-120, BULB.y, 240, 120)
    g.clip()
    g.globalAlpha = 0.55
    g.translate(hash(fi, 42) * 256, hash(fi, 43) * 256)
    g.fillStyle = speckPattern
    g.fillRect(-400, -400, 800, 800)
    g.restore()
    g.globalCompositeOperation = 'source-over'
    g.restore()
    g.fillStyle = '#e8562a'
    g.beginPath()
    licks.forEach((l) => drop(g, l.x, l.y, l.s))
    g.fill()
    // print grain on the flame, shifted each drawing so it boils
    g.globalCompositeOperation = 'destination-out'
    g.globalAlpha = 0.4
    g.save()
    g.translate(hash(fi, 40) * 256, hash(fi, 41) * 256)
    g.fillStyle = speckPattern
    g.fillRect(VL - 300, VT - 300, VR - VL + 600, VB - VT + 600)
    g.restore()
    g.globalAlpha = 1
    g.globalCompositeOperation = 'source-over'

    ctx.setTransform(1, 0, 0, 1, 0, 0)
    ctx.save()
    ctx.shadowColor = `rgba(245,110,40,${0.45 * flick})`
    ctx.shadowBlur = 26 * dpr * scale
    ctx.globalAlpha = 0.92
    ctx.drawImage(fireLayer, 0, 0)
    ctx.restore()
  }

  const embers: { x: number; y: number; vx: number; vy: number; life: number; max: number; s: number; ph: number }[] = []
  let emberClock = 0
  function drawEmbers(t: number, dt: number, stoke: number) {
    toDesign(ctx)
    emberClock += dt
    const every = 0.14 / Math.max(0.25, 1 + 3 * stoke)
    while (emberClock > every) {
      emberClock -= every
      embers.push({
        x: FIRE_X + R(-22, 22), y: 560 + R(-10, 20), vx: R(-12, 12),
        vy: -R(45, 95) * (1 + Math.max(0, stoke)), life: 0, max: R(1.2, 2.6), s: R(1.4, 2.8), ph: R(0, 6),
      })
    }
    for (let i = embers.length - 1; i >= 0; i--) {
      const e = embers[i]
      e.life += dt
      if (e.life > e.max) {
        embers.splice(i, 1)
        continue
      }
      e.x += (e.vx + Math.sin(t * 3 + e.ph) * 18) * dt
      e.y += e.vy * dt
      const k = 1 - e.life / e.max, fl = 0.6 + 0.4 * Math.sin(t * 20 + e.ph)
      ctx.fillStyle = `rgba(${(200 + 40 * k) | 0},${(70 + 60 * k) | 0},30,${k * fl})`
      ctx.fillRect(e.x, e.y, e.s, e.s * 1.4)
    }
  }

  // ---------- the spirit ----------
  // Round eyes in the yellow core, and a mouth that only shows when he has
  // something to say: at rest, focused or asleep it's just the eyes. He blinks,
  // looks around on his own, follows the cursor, and wears the mood the page sets.
  // Each mood is a set of numbers the face eases toward, so moods blend.
  interface Face { lid: number; eye: number; pupil: number; mouth: number; smile: number; w: number }
  const MOODS: Record<Mood, Face> = {
    //          lid: eyes shut (0 or 1)   eye / pupil: sizes   mouth: how visible   smile: 0 flat .. 1 smile   w: mouth width
    idle:      { lid: 0, eye: 1,    pupil: 1,    mouth: 0, smile: 0.5, w: 0.7 },
    curious:   { lid: 0, eye: 1.04, pupil: 1,    mouth: 1, smile: 0,   w: 0.5 },
    focused:   { lid: 0, eye: 1,    pupil: 0.92, mouth: 0, smile: 0,   w: 0.5 },
    happy:     { lid: 0, eye: 1.04, pupil: 1.15, mouth: 1, smile: 1,   w: 0.9 },
    delighted: { lid: 0, eye: 1.08, pupil: 1.2,  mouth: 1, smile: 1,   w: 1.15 },
    sleepy:    { lid: 1, eye: 1,    pupil: 1,    mouth: 0, smile: 0,   w: 0.45 },
  }
  const face = { ...MOODS.idle, gx: 0, gy: 0, blink: 0 }
  let mood: Mood = 'idle', moodUntil = 0, moodNext: Mood = 'idle'
  let gazeTarget: Pt = [0, 0], nextGlance = 1.5, nextBlink = 2.5, blinkAt = -1
  let pointer: Pt | null = null, pointerAt = -99, lookAtPt: Pt | null = null, lookUntil = 0
  let lastInput = 0, clock = 0
  const zs: { a: number }[] = []
  const FACE_DY = -84, EYE_DX = 23, FACE_ALPHA = 0.72
  const INK_LINE = 'rgba(92,34,12,.9)', SCLERA = '#fbe6be', PUPIL = '#3a160a'

  function setMood(name: Mood, secs = 0, then: Mood = 'idle') {
    mood = name
    moodUntil = secs ? clock + secs : 0
    moodNext = then
  }
  // where the face is on the page, for aiming the eyes at things
  function faceClient(): Pt {
    const r = canvas.getBoundingClientRect()
    return [r.left + FIRE_X * scale + ox, r.top + (FLAME_Y + FACE_DY) * scale + oy]
  }
  function aimAt(x: number, y: number): Pt {
    const [fx, fy] = faceClient(), dx = x - fx, dy = y - fy
    const d = Math.hypot(dx, dy) || 1, m = Math.min(1, d / 160)
    return [(dx / d) * m, (dy / d) * m * 0.8]
  }

  function updateFace(dt: number) {
    clock += dt
    if (moodUntil && clock > moodUntil) {
      mood = moodNext
      moodUntil = 0
    }
    // nodding off: nothing has happened for a while
    if (mood === 'idle' && clock - lastInput > 22) mood = 'sleepy'
    const target = MOODS[mood], ease = Math.min(1, dt * 9)
    for (const k of Object.keys(target) as (keyof Face)[]) face[k] += (target[k] - face[k]) * ease

    // gaze: something to look at > a recent cursor > glancing around on his own
    if (lookAtPt && clock < lookUntil) gazeTarget = aimAt(...lookAtPt)
    else if (pointer && clock - pointerAt < 2.5) gazeTarget = aimAt(...pointer)
    else if (clock > nextGlance) {
      const spots: Pt[] = [[0, 0], [0, 0], [-0.9, 0.1], [0.9, 0.1], [-0.7, -0.6], [0.6, -0.5], [-0.6, 0.8], [0.2, 0.9]]
      gazeTarget = spots[Math.floor(Math.random() * spots.length)]
      nextGlance = clock + 1.2 + Math.random() * 2.8
    }
    if (mood === 'sleepy') gazeTarget = [0, 0.3]
    const sac = Math.min(1, dt * 16)   // eyes snap, they don't drift
    face.gx += (gazeTarget[0] - face.gx) * sac
    face.gy += (gazeTarget[1] - face.gy) * sac

    // blinking, now and then twice
    if (clock > nextBlink) {
      blinkAt = clock
      nextBlink = clock + (Math.random() < 0.2 ? 0.32 : 2.2 + Math.random() * 4)
    }
    const bt = clock - blinkAt
    face.blink = bt >= 0 && bt < 0.16 ? Math.sin((bt / 0.16) * Math.PI) : 0

    if (mood === 'sleepy' && Math.random() < dt * 0.7) zs.push({ a: 0 })
  }

  // Always a circle; when shut (a blink, or asleep) it's a clean closed curve,
  // swapped in whole like a drawn frame rather than a lid sliding down.
  function drawEye(g: CanvasRenderingContext2D, ex: number, ey: number) {
    const r = 12.5 * face.eye
    g.lineWidth = 1.7
    g.strokeStyle = INK_LINE
    g.lineCap = 'round'
    if (Math.max(face.lid, face.blink) > 0.5) {
      g.beginPath()
      g.moveTo(ex - r * 0.85, ey + 1)
      g.quadraticCurveTo(ex, ey + r * 0.55, ex + r * 0.85, ey + 1)
      g.lineWidth = 2.1
      g.stroke()
      return
    }
    g.beginPath()
    g.arc(ex, ey, r, 0, 7)
    g.fillStyle = SCLERA
    g.fill()
    g.save()
    g.clip()
    const pr = 5.2 * face.pupil, room = r - pr - 1.5
    g.fillStyle = PUPIL
    g.beginPath()
    g.arc(ex + face.gx * room, ey + face.gy * room * 0.9, pr, 0, 7)
    g.fill()
    g.restore()
    g.beginPath()
    g.arc(ex, ey, r, 0, 7)
    g.stroke()
  }

  function drawMouth(g: CanvasRenderingContext2D, mx: number, my: number) {
    if (face.mouth < 0.02) return
    const w = 8.5 * face.w
    g.save()
    g.globalAlpha = face.mouth
    g.strokeStyle = INK_LINE
    g.lineWidth = 1.9
    g.lineCap = 'round'
    g.beginPath()
    g.moveTo(mx - w, my)
    g.quadraticCurveTo(mx, my + face.smile * 7, mx + w, my)
    g.stroke()
    g.restore()
  }

  // The face glows through the flame rather than sitting on it: its own layer,
  // laid in semi-transparent, with the flame's grain knocked through.
  function drawFace(fi: number, stoke: number) {
    const g = faceLayer.getContext('2d')!
    g.setTransform(1, 0, 0, 1, 0, 0)
    g.clearRect(0, 0, faceLayer.width, faceLayer.height)
    toDesign(g)
    const sy = 1 + 0.14 * stoke
    // the face rides the flame: a slow sway plus a small step with each drawing
    const jx = (hash(fi, 51) - 0.5) * 1.4, jy = (hash(fi, 52) - 0.5) * 1.2
    const cx = FLAME_X - 2 + Math.sin(clock * 1.3) * 1.8 + jx + face.gx * 2.5
    const cy = FLAME_Y + FACE_DY * sy + jy + face.gy * 2
    drawEye(g, cx - EYE_DX, cy - 4)
    drawEye(g, cx + EYE_DX, cy - 4)
    drawMouth(g, cx + face.gx * 1.5, cy + 15)
    g.setTransform(1, 0, 0, 1, 0, 0)
    g.globalCompositeOperation = 'destination-out'
    g.globalAlpha = 0.3
    g.save()
    g.translate(hash(fi, 60) * 256, hash(fi, 61) * 256)
    g.fillStyle = speckPattern
    g.fillRect(-256, -256, faceLayer.width + 512, faceLayer.height + 512)
    g.restore()
    g.globalAlpha = 1
    g.globalCompositeOperation = 'source-over'

    ctx.setTransform(1, 0, 0, 1, 0, 0)
    ctx.save()
    ctx.globalAlpha = FACE_ALPHA
    ctx.drawImage(faceLayer, 0, 0)
    ctx.restore()

    // z's drifting up while he sleeps
    toDesign(ctx)
    ctx.save()
    ctx.fillStyle = '#2a1710'
    ctx.font = 'italic 600 13px "Source Serif 4 Variable", Georgia, serif'
    for (let i = zs.length - 1; i >= 0; i--) {
      const z = zs[i]
      z.a += 0.016
      if (z.a > 1 || mood !== 'sleepy') {
        zs.splice(i, 1)
        continue
      }
      ctx.globalAlpha = Math.sin(z.a * Math.PI) * 0.7
      ctx.save()
      ctx.translate(cx + 34 + z.a * 18 + Math.sin(z.a * 6) * 4, cy - 40 - z.a * 60)
      ctx.scale(0.7 + z.a * 0.6, 0.7 + z.a * 0.6)
      ctx.fillText('z', 0, 0)
      ctx.restore()
    }
    ctx.restore()
  }

  let stokeLevel = 0, stokeTarget = 0, baseline = 0
  let last = performance.now()
  const start = last
  function render(now: number) {
    const t = (now - start) / 1000, dt = Math.min(0.05, (now - last) / 1000)
    last = now
    // asleep, the fire burns low
    baseline += ((mood === 'sleepy' ? -0.45 : 0) - baseline) * Math.min(1, dt * 1.5)
    stokeTarget *= Math.exp(-dt * 1.2)
    stokeLevel += (stokeTarget + baseline - stokeLevel) * Math.min(1, dt * 6)
    updateFace(dt)
    const tf = Math.floor(t * FLAME_FPS) / FLAME_FPS
    const flick = 1 + wob(tf * 2.2, 9) * 0.18 + 0.3 * stokeLevel

    ctx.setTransform(1, 0, 0, 1, 0, 0)
    ctx.clearRect(0, 0, canvas.width, canvas.height)
    // firelight: a warm stain on the paper, not an additive glow
    toDesign(ctx)
    const gl = ctx.createRadialGradient(FIRE_X, 640, 10, FIRE_X, 640, 260 * flick)
    gl.addColorStop(0, `rgba(244,130,60,${0.2 * flick})`)
    gl.addColorStop(0.4, `rgba(236,120,60,${0.07 * flick})`)
    gl.addColorStop(1, 'rgba(236,120,60,0)')
    ctx.fillStyle = gl
    ctx.fillRect(VL, VT, VR - VL, VB - VT)
    ctx.setTransform(1, 0, 0, 1, 0, 0)
    ctx.drawImage(staticLayer, 0, 0)

    const fi = Math.floor(t * FLAME_FPS)
    drawFire(fi, flick, stokeLevel)
    drawFace(fi, stokeLevel)
    ctx.setTransform(1, 0, 0, 1, 0, 0)
    ctx.drawImage(frontLogLayer, 0, 0)
    drawEmbers(t, dt, stokeLevel)
  }

  // ---------- shell ----------
  let raf = 0, ready = false, visible = true, stopped = false
  function frame(now: number) {
    raf = 0
    if (!ready) ready = resize()
    if (ready) render(now)
    schedule()
  }
  function schedule() {
    if (!raf && !stopped && visible && !document.hidden) raf = requestAnimationFrame(frame)
  }
  const ro = new ResizeObserver(() => {
    ready = resize()
  })
  ro.observe(canvas)
  const io = new IntersectionObserver(([e]) => {
    visible = e.isIntersecting
    schedule()
  })
  io.observe(canvas)
  const onVis = () => {
    last = performance.now()
    schedule()
  }
  document.addEventListener('visibilitychange', onVis)
  // any sign of life: note it, and if he was asleep, he wakes and looks around
  const wake = () => {
    lastInput = clock
    if (mood === 'sleepy') {
      setMood('curious', 1)
      stokeTarget = Math.min(1, stokeTarget + 0.5)
    }
  }
  const onMove = (e: PointerEvent) => {
    pointer = [e.clientX, e.clientY]
    pointerAt = clock
    wake()
  }
  window.addEventListener('pointermove', onMove, { passive: true })
  window.addEventListener('keydown', wake)
  window.addEventListener('scroll', wake, { passive: true })
  schedule()

  return {
    stoke(amount) {
      stokeTarget = Math.min(1, stokeTarget + amount)
    },
    mood(name, secs, then) {
      wake()
      setMood(name, secs, then)
    },
    look(x, y, secs = 1.2) {
      lookAtPt = [x, y]
      lookUntil = clock + secs
    },
    blink() {
      blinkAt = clock
      nextBlink = clock + 0.3
    },
    hit(x, y) {
      const [fx, fy] = faceClient()
      return Math.hypot(x - fx, (y - fy) * 0.8) < 70 * scale
    },
    get currentMood() {
      return mood
    },
    stop() {
      stopped = true
      cancelAnimationFrame(raf)
      ro.disconnect()
      io.disconnect()
      document.removeEventListener('visibilitychange', onVis)
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('keydown', wake)
      window.removeEventListener('scroll', wake)
    },
  }
}
