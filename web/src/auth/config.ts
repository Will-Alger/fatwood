// Entra External ID (CIAM) app registration values. These are public
// identifiers, not secrets — the SPA is a public client by definition.
// Overridable per environment via Vite env vars.

export const AUTH_CONFIG = {
  clientId: import.meta.env.VITE_AUTH_CLIENT_ID ?? 'cd50bde4-0c8e-4d79-8664-a59787950fe9',
  authority:
    import.meta.env.VITE_AUTH_AUTHORITY ??
    'https://fatwoodio.ciamlogin.com/f0ae24f7-e027-456f-bd3d-ec966ffec496',
  knownAuthority: 'fatwoodio.ciamlogin.com',
  apiScope:
    import.meta.env.VITE_AUTH_API_SCOPE ??
    'api://f3ab7456-495b-4264-af68-963341125d4e/access_as_user',
} as const

export const LOGIN_SCOPES = ['openid', 'profile', 'email', AUTH_CONFIG.apiScope]
