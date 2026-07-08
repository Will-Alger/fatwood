import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Kestrel is pinned to 5080 in launchSettings.json; all client calls use
    // relative /api paths so dev (proxied) and prod (same origin) both work.
    proxy: {
      '/api': 'http://localhost:5080',
    },
  },
})
