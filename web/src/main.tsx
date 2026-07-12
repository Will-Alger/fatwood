import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
// Self-hosted fonts (bundled by Vite — no external requests, no layout flash):
// Plex Sans for UI/body, Source Serif for paper titles + wordmark, Plex Mono
// for category codes and badges.
import '@fontsource-variable/ibm-plex-sans/index.css'
import '@fontsource-variable/source-serif-4/index.css'
import '@fontsource/ibm-plex-mono/400.css'
import '@fontsource/ibm-plex-mono/600.css'
import './index.css'
import App from './App.tsx'

// Apply the persisted theme before first paint so there is no flash.
// Dark is the primary theme; light is the opt-in.
const storedTheme = localStorage.getItem('fatwood.theme')
document.documentElement.dataset.theme = storedTheme === 'light' ? 'light' : 'dark'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
