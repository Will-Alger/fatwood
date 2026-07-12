import {
  InteractionRequiredAuthError,
  PublicClientApplication,
  type AccountInfo,
} from '@azure/msal-browser'
import { AUTH_CONFIG, LOGIN_SCOPES } from './config'

// One MSAL instance for the app. Redirect flow (not popup): it works on
// mobile and inside strict popup blockers, and the app is fine with a full
// page round-trip on sign-in.
const msal = new PublicClientApplication({
  auth: {
    clientId: AUTH_CONFIG.clientId,
    authority: AUTH_CONFIG.authority,
    knownAuthorities: [AUTH_CONFIG.knownAuthority],
    redirectUri: window.location.origin,
    postLogoutRedirectUri: window.location.origin,
  },
  cache: {
    // localStorage so the session survives tab closes; tokens are short-lived
    // and refresh silently.
    cacheLocation: 'localStorage',
  },
})

let ready: Promise<void> | null = null

/** Must complete before anything calls getAccessToken (handles the redirect return leg). */
export function initAuth(): Promise<void> {
  ready ??= (async () => {
    await msal.initialize()
    const result = await msal.handleRedirectPromise()
    if (result?.account) {
      msal.setActiveAccount(result.account)
    } else if (!msal.getActiveAccount() && msal.getAllAccounts().length > 0) {
      msal.setActiveAccount(msal.getAllAccounts()[0])
    }
  })()
  return ready
}

export function getAccount(): AccountInfo | null {
  return msal.getActiveAccount()
}

export function signIn(): Promise<void> {
  return msal.loginRedirect({ scopes: [...LOGIN_SCOPES] })
}

export function signOut(): Promise<void> {
  return msal.logoutRedirect()
}

/**
 * Bearer token for the API, or null when nobody is signed in (anonymous
 * browse, or local dev where the server authenticates everything itself).
 * Silent first; an expired session falls back to the redirect flow.
 */
export async function getAccessToken(): Promise<string | null> {
  await initAuth()
  const account = msal.getActiveAccount()
  if (!account) return null

  try {
    const result = await msal.acquireTokenSilent({
      scopes: [AUTH_CONFIG.apiScope],
      account,
    })
    return result.accessToken
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      await msal.acquireTokenRedirect({ scopes: [AUTH_CONFIG.apiScope], account })
      return null // unreachable: redirect navigates away
    }
    throw error
  }
}
