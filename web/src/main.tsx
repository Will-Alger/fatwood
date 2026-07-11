import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
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
