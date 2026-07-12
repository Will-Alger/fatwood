import { useState } from 'react'
import {
  SignInCodeRequiredState,
  SignUpCodeRequiredState,
  SignUpPasswordRequiredState,
  ResetPasswordCodeRequiredState,
  ResetPasswordPasswordRequiredState,
} from '@azure/msal-browser/custom-auth'
import { LOGIN_SCOPES } from '../auth/config'
import { getAuthApp } from '../auth/auth'
import { Logo } from './Logo'

type Mode = 'signin' | 'signup' | 'reset'
type Step = 'form' | 'code' | 'newPassword'

interface AuthPanelProps {
  onClose: () => void
  /** Called once the user is fully signed in — the caller refetches /api/me. */
  onSignedIn: () => void
}

/**
 * Native-auth sign-in/sign-up/reset, rendered as Fatwood UI (no redirect, no
 * hosted page). Drives the MSAL custom-auth state machine: each submit either
 * completes, advances to the next state (email code, new password), or
 * surfaces a friendly error.
 */
export function AuthPanel({ onClose, onSignedIn }: AuthPanelProps) {
  const [mode, setMode] = useState<Mode>('signin')
  const [step, setStep] = useState<Step>('form')
  const [email, setEmail] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // The SDK state object awaiting the next input (code / password).
  const [flowState, setFlowState] = useState<unknown>(null)

  function switchMode(next: Mode) {
    setMode(next)
    setStep('form')
    setError(null)
    setPassword('')
    setCode('')
    setFlowState(null)
  }

  function fail(message: string) {
    setError(message)
    setBusy(false)
  }

  function describeError(err: unknown): string {
    const e = err as {
      isUserNotFound?: () => boolean
      isUserAlreadyExists?: () => boolean
      isPasswordIncorrect?: () => boolean
      isInvalidPassword?: () => boolean
      isInvalidCode?: () => boolean
      isRedirectRequired?: () => boolean
      errorData?: { errorDescription?: string }
    }
    if (e?.isUserNotFound?.()) return 'No account with that email — create one below.'
    if (e?.isUserAlreadyExists?.()) return 'An account with that email already exists — sign in instead.'
    if (e?.isPasswordIncorrect?.()) return 'Incorrect password. You can reset it below.'
    if (e?.isInvalidPassword?.()) return 'That password is too weak — use at least 8 characters with some variety.'
    if (e?.isInvalidCode?.()) return 'That code is not right — check the email and try again.'
    if (e?.isRedirectRequired?.()) return 'This account needs the standard sign-in flow. Contact the operator.'
    return e?.errorData?.errorDescription ?? 'Something went wrong — try again.'
  }

  /** Route any flow result: done, next step, or error. */
  async function handleResult(result: {
    isCompleted: () => boolean
    isFailed: () => boolean
    error?: unknown
    state?: unknown
    data?: unknown
  }): Promise<void> {
    if (result.isFailed()) {
      fail(describeError(result.error))
      return
    }

    const state = result.state
    if (
      state instanceof SignInCodeRequiredState ||
      state instanceof SignUpCodeRequiredState ||
      state instanceof ResetPasswordCodeRequiredState
    ) {
      setFlowState(state)
      setStep('code')
      setCode('')
      setError(null)
      setBusy(false)
      return
    }

    if (state instanceof SignUpPasswordRequiredState) {
      // We always collect the password up front, but the flow can still ask.
      const next = await (state as SignUpPasswordRequiredState).submitPassword(password)
      await handleResult(next as never)
      return
    }

    if (state instanceof ResetPasswordPasswordRequiredState) {
      setFlowState(state)
      setStep('newPassword')
      setPassword('')
      setError(null)
      setBusy(false)
      return
    }

    if (result.isCompleted()) {
      // Sign-up / reset completions expose signIn() to finish without a
      // second code; sign-in completion is already signed in.
      const completed = result.data as { signIn?: (args?: object) => Promise<never> } | undefined
      const stateSignIn = (result.state as { signIn?: (args?: object) => Promise<never> })?.signIn
      if (mode !== 'signin') {
        const signInFn = stateSignIn ?? completed?.signIn
        if (signInFn) {
          const signedIn = await signInFn.call(result.state ?? result.data, {
            scopes: [...LOGIN_SCOPES],
          })
          await handleResult(signedIn as never)
          return
        }
      }

      setBusy(false)
      onSignedIn()
      onClose()
      return
    }

    fail('Unexpected sign-in state — try again.')
  }

  async function submitForm() {
    setBusy(true)
    setError(null)
    try {
      const app = await getAuthApp()
      if (mode === 'signin') {
        const result = await app.signIn({
          username: email.trim(),
          password,
          scopes: [...LOGIN_SCOPES],
        })
        await handleResult(result as never)
      } else if (mode === 'signup') {
        const result = await app.signUp({
          username: email.trim(),
          password,
          attributes: { displayName: displayName.trim() || email.trim() },
        })
        await handleResult(result as never)
      } else {
        const result = await app.resetPassword({ username: email.trim() })
        await handleResult(result as never)
      }
    } catch (err) {
      fail(err instanceof Error ? err.message : 'Sign-in failed — try again.')
    }
  }

  async function submitCode() {
    setBusy(true)
    setError(null)
    try {
      const state = flowState as { submitCode: (code: string) => Promise<never> }
      const result = await state.submitCode(code.trim())
      await handleResult(result as never)
    } catch (err) {
      fail(err instanceof Error ? err.message : 'Verification failed — try again.')
    }
  }

  const [resent, setResent] = useState(false)

  async function resendCode() {
    setBusy(true)
    setError(null)
    setResent(false)
    try {
      const state = flowState as { resendCode?: () => Promise<never> }
      if (!state.resendCode) {
        fail('Could not resend — go back and start over.')
        return
      }
      const result = (await state.resendCode()) as {
        isFailed: () => boolean
        error?: unknown
        state?: unknown
      }
      if (result.isFailed()) {
        fail(describeError(result.error))
        return
      }
      // Resending yields a fresh code-required state; keep driving that one.
      if (result.state) {
        setFlowState(result.state)
      }
      setResent(true)
      setBusy(false)
    } catch (err) {
      fail(err instanceof Error ? err.message : 'Could not resend the code.')
    }
  }

  async function submitNewPassword() {
    setBusy(true)
    setError(null)
    try {
      const state = flowState as { submitNewPassword: (pw: string) => Promise<never> }
      const result = await state.submitNewPassword(password)
      await handleResult(result as never)
    } catch (err) {
      fail(err instanceof Error ? err.message : 'Password reset failed — try again.')
    }
  }

  const titles: Record<Mode, string> = {
    signin: 'Sign in',
    signup: 'Create your account',
    reset: 'Reset password',
  }

  return (
    <div className="settings-overlay auth-overlay" onClick={onClose}>
      <div className="auth-panel" onClick={(e) => e.stopPropagation()}>
        <div className="auth-brand">
          <Logo size={40} />
          <h2>{step === 'code' ? 'Check your email' : titles[mode]}</h2>
        </div>

        {error && <p className="status status-error">{error}</p>}

        {step === 'form' && (
          <form
            className="auth-form"
            onSubmit={(e) => {
              e.preventDefault()
              void submitForm()
            }}
          >
            {mode === 'signup' && (
              <label>
                Name
                <input
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                  autoComplete="name"
                  placeholder="How should we address you?"
                />
              </label>
            )}
            <label>
              Email
              <input
                type="email"
                required
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                autoComplete="email"
                placeholder="you@example.com"
              />
            </label>
            {mode !== 'reset' && (
              <label>
                Password
                <input
                  type="password"
                  required
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  autoComplete={mode === 'signup' ? 'new-password' : 'current-password'}
                  placeholder={mode === 'signup' ? 'At least 8 characters' : 'Your password'}
                />
              </label>
            )}
            <button type="submit" className="primary-button" disabled={busy || !email.trim()}>
              {busy
                ? 'Working…'
                : mode === 'signin'
                  ? 'Sign in'
                  : mode === 'signup'
                    ? 'Create account'
                    : 'Send reset code'}
            </button>
          </form>
        )}

        {step === 'code' && (
          <form
            className="auth-form"
            onSubmit={(e) => {
              e.preventDefault()
              void submitCode()
            }}
          >
            <p className="settings-hint">
              We emailed a verification code to <strong>{email.trim()}</strong>. Enter it here —
              and check your spam folder, the sender is Microsoft on our behalf.
            </p>
            {resent && <p className="status status-notice">A fresh code is on its way.</p>}
            <label>
              Verification code
              <input
                inputMode="numeric"
                autoComplete="one-time-code"
                required
                value={code}
                onChange={(e) => setCode(e.target.value)}
                placeholder="12345678"
              />
            </label>
            <button type="submit" className="primary-button" disabled={busy || !code.trim()}>
              {busy ? 'Verifying…' : 'Verify'}
            </button>
            <button
              type="button"
              className="link-button"
              disabled={busy}
              onClick={() => void resendCode()}
            >
              Didn't get it? Resend the code
            </button>
          </form>
        )}

        {step === 'newPassword' && (
          <form
            className="auth-form"
            onSubmit={(e) => {
              e.preventDefault()
              void submitNewPassword()
            }}
          >
            <label>
              New password
              <input
                type="password"
                required
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete="new-password"
                placeholder="At least 8 characters"
              />
            </label>
            <button type="submit" className="primary-button" disabled={busy || password.length === 0}>
              {busy ? 'Saving…' : 'Set new password'}
            </button>
          </form>
        )}

        {step === 'form' && (
          <div className="auth-links">
            {mode !== 'signin' && (
              <button type="button" className="link-button" onClick={() => switchMode('signin')}>
                Have an account? Sign in
              </button>
            )}
            {mode === 'signin' && (
              <>
                <button type="button" className="link-button" onClick={() => switchMode('signup')}>
                  New here? Create an account
                </button>
                <button type="button" className="link-button" onClick={() => switchMode('reset')}>
                  Forgot password?
                </button>
              </>
            )}
          </div>
        )}

        <p className="auth-fine-print">
          By continuing you agree to the <a href="/terms.html">terms</a> and{' '}
          <a href="/privacy.html">privacy policy</a>.
        </p>
      </div>
    </div>
  )
}
