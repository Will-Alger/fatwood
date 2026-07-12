import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Kestrel is pinned to 5080 in launchSettings.json; all client calls use
    // relative /api paths so dev (proxied) and prod (same origin) both work.
    // VITE_API_PROXY overrides the target, e.g. http://localhost:8080 to use
    // the compose api container instead of `dotnet run`.
    proxy: {
      '/api': process.env.VITE_API_PROXY ?? 'http://localhost:5080',
    },
  },
})
