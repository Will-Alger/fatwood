/**
 * The Fatwood mark: a resin-rich pine splinter, lit only at the tip.
 *
 * Two flat polygons and no gradient, no glow — the whole theme's argument is
 * that ignition is an event, not a colour, so the mark spends its one ember on
 * the catching corner and leaves the wood achromatic. Both fills are CSS
 * variables, so the mark re-inks itself between Char and Flashpoint.
 */
export function Logo({ size = 34 }: { size?: number }) {
  return (
    <svg
      width={Math.round((size * 30) / 34)}
      height={size}
      viewBox="0 0 30 34"
      fill="none"
      role="img"
      aria-label="Fatwood"
    >
      {/* the splinter */}
      <polygon points="4,34 11,31 22,3 15,1" className="fw-logo-wood" />
      {/* the tip, catching */}
      <polygon points="22,3 15,1 18,9 24,10" className="fw-logo-tip" />
    </svg>
  )
}
