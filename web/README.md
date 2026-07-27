# Fatwood — web client

React + TypeScript (Vite) SPA for [Fatwood](../README.md). No UI framework, no
CSS framework, and plain `fetch` rather than a query library — the app has a
handful of endpoints and doesn't justify the dependency.

In production this is built and served as static files by the ASP.NET Core API
from the same container (see the repo `Dockerfile`, frontend build stage), so
there is no CORS configuration and no second origin.

## Dev loop

```bash
npm install
npm run dev      # http://localhost:5173; proxies /api → http://localhost:5080
```

The API must be running separately — see [docs/running.md](../docs/running.md).
With `Auth:Authority` unset the API auto-signs you in as a local dev **Owner**,
so the whole UI (including Settings and admin panels) works with no tenant
setup, no tokens, and no invite code.

To point the dev server at the packaged container instead of `dotnet run`:

```bash
VITE_API_PROXY=http://localhost:8080 npm run dev
```

Other scripts:

```bash
npm run build    # tsc -b (type-check) + production bundle into dist/
npm run lint     # oxlint
npm run preview  # serve the built bundle locally
```

## Layout

| Path | What |
|---|---|
| `src/api/` | Typed client + DTOs mirroring the server contracts |
| `src/components/` | Discover (search), Browse, paper cards, settings/admin panels |
| `src/hooks/` | Data-fetching hooks (abortable, primitive-keyed effects) |
| `src/App.tsx` | Tab shell, theme, auth-aware layout |

`src/api/types.ts` is hand-written, not generated — server DTO changes must be
mirrored there by hand.
