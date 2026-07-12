import {
  CustomAuthPublicClientApplication,
  type CustomAuthConfiguration,
  type ICustomAuthPublicClientApplication,
} from '@azure/msal-browser/custom-auth'
import { AUTH_CONFIG } from './config'

// Native authentication: sign-in/sign-up/reset render as our own UI and the
// SDK talks to the tenant through our same-origin /auth-proxy (the native
// auth API has no CORS support by design). The user never leaves fatwood.io.
const config: CustomAuthConfiguration = {
  customAuth: {
    challengeTypes: ['password', 'oob', 'redirect'],
    authApiProxyUrl: `${window.location.origin}/auth-proxy`,
  },
  auth: {
    clientId: AUTH_CONFIG.clientId,
    authority: `https://${AUTH_CONFIG.knownAuthority}`,
    redirectUri: '/',
    postLogoutRedirectUri: '/',
  },
  cache: {
    // localStorage so the session survives tab closes; tokens are short-lived
    // and refresh silently via the cached refresh token.
    cacheLocation: 'localStorage',
  },
}

let appPromise: Promise<ICustomAuthPublicClientApplication> | null = null

function getApp(): Promise<ICustomAuthPublicClientApplication> {
  appPromise ??= CustomAuthPublicClientApplication.create(config)
  return appPromise
}

/** Kept for main.tsx: ensures the SDK is initialized before first render. */
export async function initAuth(): Promise<void> {
  await getApp()
}

/** The SDK instance — the AuthPanel drives sign-in/sign-up/reset flows on it. */
export function getAuthApp(): Promise<ICustomAuthPublicClientApplication> {
  return getApp()
}

/**
 * Bearer token for the API, or null when nobody is signed in (anonymous
 * browse, or local dev where the server authenticates everything itself).
 * Served from cache; silently renewed with the refresh token when expired.
 */
export async function getAccessToken(): Promise<string | null> {
  const app = await getApp()
  const account = app.getCurrentAccount()
  if (!account.data) return null

  const result = await account.data.getAccessToken({
    forceRefresh: false,
    scopes: [AUTH_CONFIG.apiScope],
  })
  return result.data?.accessToken ?? null
}

export async function signOut(): Promise<void> {
  const app = await getApp()
  const account = app.getCurrentAccount()
  if (account.data) {
    await account.data.signOut()
  }
}
