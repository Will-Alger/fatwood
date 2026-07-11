/**
 * The Fatwood mark: a resin-rich pine splinter catching at the tip.
 * This is deliberately the ONLY element in the app with a glow — a faint
 * radial ember behind the flame. Everything else stays matte.
 */
export function Logo({ size = 34 }: { size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 64 64"
      fill="none"
      role="img"
      aria-label="Fatwood"
    >
      <defs>
        <linearGradient id="fw-wood" x1="20" y1="58" x2="42" y2="14" gradientUnits="userSpaceOnUse">
          <stop offset="0" stopColor="#5b4128" />
          <stop offset="0.62" stopColor="#8a5f33" />
          <stop offset="1" stopColor="#c98a4b" />
        </linearGradient>
        <linearGradient id="fw-flame" x1="40" y1="2" x2="44" y2="18" gradientUnits="userSpaceOnUse">
          <stop offset="0" stopColor="#f6b356" />
          <stop offset="1" stopColor="#e06a2e" />
        </linearGradient>
        <radialGradient id="fw-glow" cx="0.5" cy="0.5" r="0.5">
          <stop offset="0" stopColor="#f0904f" stopOpacity="0.45" />
          <stop offset="1" stopColor="#f0904f" stopOpacity="0" />
        </radialGradient>
      </defs>

      {/* the one permitted glow */}
      <circle cx="42" cy="11" r="13" fill="url(#fw-glow)" />

      {/* splinter */}
      <path
        d="M16 59 L23 56 L42 17 L36 15 Z"
        fill="url(#fw-wood)"
      />
      {/* resin grain lines */}
      <path d="M21.5 51 L36.5 20" stroke="#3d2c19" strokeWidth="1.1" strokeLinecap="round" opacity="0.55" />
      <path d="M25.5 49 L38.8 21.5" stroke="#e8b877" strokeWidth="0.9" strokeLinecap="round" opacity="0.5" />

      {/* flame at the tip */}
      <path
        d="M41.5 3 C45.6 8 48 11.4 45.2 15.3 C43.6 17.5 40 17.6 38.4 15.4 C36 12.2 38 7.4 41.5 3 Z"
        fill="url(#fw-flame)"
      />
      <path
        d="M41.6 8.5 C43.3 10.6 44 12.3 42.8 13.9 C42 14.9 40.5 14.9 39.8 13.8 C38.8 12.3 39.9 10.3 41.6 8.5 Z"
        fill="#fbe0b4"
        opacity="0.9"
      />
    </svg>
  )
}
