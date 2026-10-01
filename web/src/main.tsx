import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
// Self-hosted fonts (bundled by Vite — no external requests, no layout flash):
// Archivo for chrome (wordmark, labels, buttons), Source Serif for paper
// titles and long-form prose, Plex Mono for every number and identifier.
import '@fontsource-variable/archivo/index.css'
import '@fontsource-variable/source-serif-4/index.css'
import '@fontsource-variable/source-serif-4/opsz-italic.css'
import '@fontsource/ibm-plex-mono/400.css'
import '@fontsource/ibm-plex-mono/600.css'
import './index.css'
import App from './App.tsx'
import { initAuth } from './auth/auth.ts'

// Fatwood is light-only. Drop the preference key the old light/dark toggle left behind.
try {
  localStorage.removeItem('fatwood.theme')
} catch {
  // storage blocked (private mode, site data off): nothing to clean up
}

// MSAL must process a possible sign-in redirect return before the app renders,
// or the first /api/me call races the token cache. Render regardless of the
// outcome — the UI degrades to the signed-out state.
initAuth()
  .catch(() => undefined)
  .finally(() => {
    createRoot(document.getElementById('root')!).render(
      <StrictMode>
        <App />
      </StrictMode>,
    )
  })
