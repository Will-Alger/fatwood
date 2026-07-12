# Accounts, budgets, and the auth architecture

Fatwood is multi-user and publicly reachable: anyone can browse anonymously,
anyone can create an account, and every account gets a real-money budget that
bounds what they can spend. This doc explains how identity, cost control, and
the sign-in experience are built.

## Identity: Entra External ID, rendered natively

Identity (credentials, email verification, password reset) lives in a
Microsoft Entra External ID tenant — passwords never touch this codebase.
What's unusual is the presentation: sign-in, sign-up, verification-code entry,
and password reset are **rendered inside the SPA** (native authentication via
`@azure/msal-browser/custom-auth`), themed like the rest of the app in light
or dark mode. The address bar never leaves fatwood.io.

Two pieces make that work:

- **`/auth-proxy`** — Entra's native-auth API deliberately has no CORS, so the
  API hosts a same-origin reverse proxy: POST-only, a strict allowlist of the
  documented native-auth paths, no cookies forwarded, and its own per-IP rate
  bucket (Entra sees our egress IP, so brute-force braking is our job).
- **`offline_access` in the sign-in scopes** — without it the token endpoint
  issues no refresh token and the SDK fails the whole sign-in. Learned live.

The API validates JWTs (authority = the tenant, audience = the API app
registration). **Tokens carry identity, never authority**: on each request,
`UserContextMiddleware` resolves (or first-time provisions) the `AppUser` row
and stamps DB-backed role/active claims. Admin is a database fact you can
change from the admin page, not a token claim you have to re-issue.

Locally, with no tenant configured, a dev fallback scheme authenticates
everything as a synthetic local admin — and production **fails fast at
startup** if auth is unconfigured, so an open deployment can't happen by
accident.

## Cost control: a ledger, not a counter

Every account gets a **$1 starter grant** (roughly a thousand paper analyses —
generous for honest use, worthless to abusers). Money is integer
micro-dollars, never floats, and the design is an append-only ledger:

- `BudgetGrants` — credits: signup grant, admin top-ups, someday Stripe
  purchases (a purchase is just another grant row; nothing gets redesigned).
- `LlmUsageEvents` — debits: every Anthropic call records the **real token
  counts from the response** (never estimates), priced from the model
  registry. Remaining budget is always derived, never stored.

Layered on top: a per-user daily call cap (circuit breaker independent of
dollars), per-user rate limits on the spending endpoints, admin accounts show
∞, and an invite-code signup gate sits behind a feature flag
(`Accounts:RequireInviteCode`) for cost-controlled launches.

Users can also attach **their own Anthropic API key**: stored encrypted (Data
Protection, key ring persisted in the database), write-only through the API —
set, replace, or delete, never read back beyond the last 4 characters. BYO
calls never debit the platform budget, and premium models (Opus) only run on
a BYO key — on the platform key they silently fall back to the step default.

## Per-user everything

Profiles, bookmarks, analyses, and search telemetry are all keyed by account.
Analyses are personalized (they read a paper against *your* profile), so the
same paper carries one analysis row per user, and staleness is tracked against
the owner's profile version. Rows created before accounts existed are claimed
by the bootstrap admin on first sign-in — the operator's history survives.

## Branded verification email

Sign-up verification codes arrive as Fatwood-branded email from
`noreply@fatwood.io`, not Microsoft's stock template: an Entra **custom email
provider extension** (OnOtpSend event) calls `/api/auth-events/otp-send`
(authenticated: its own JWT audience, caller pinned to Microsoft's
custom-extension service principal), which renders the template and sends via
Azure Communication Services on a domain with our own SPF/DKIM. Entra gives
the hook a 2-second budget — the send waits only for ACS to *accept* the
message — and any failure falls back to Microsoft's default email, so
sign-ups can never brick on our sender.

## The perimeter

Cloudflare fronts the domain (proxy + strict TLS + Bot Fight Mode + edge
redirect of the apex to www). Behind it: ASP.NET rate limiting (global
per-caller bucket + tight buckets on spending and auth endpoints), a strict
CSP and standard security headers, HSTS, and cost alarms on both the Azure
subscription and the Anthropic account. The old all-or-nothing Easy Auth wall
and the shared admin API key are fully retired.
