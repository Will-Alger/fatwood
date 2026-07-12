import { useEffect, useState } from 'react'
import {
  getLlmSettings,
  getProfile,
  removeAnthropicKey,
  saveProfile,
  setAnthropicKey,
  setLlmAssignment,
} from '../api/client'
import type { LlmSettingsView, MeView, ProfileView } from '../api/types'
import { signOut } from '../auth/auth'
import { formatBudget } from '../hooks/useMe'

const STEP_LABELS: Record<string, string> = {
  QueryCompiler: 'Query compiler (runs once per search)',
  PaperAnalysis: 'Paper analysis (runs once per paper)',
  RelevanceJudge: 'Relevance judge (offline evaluation only)',
}

interface SettingsPanelProps {
  me: MeView | null
  signedOut: boolean
  onClose: () => void
  onSettingsChanged: (settings: LlmSettingsView | null) => void
  /** Called after account-level changes (BYO key set/removed) so /api/me refetches. */
  onAccountChanged?: () => void
}

export function SettingsPanel({
  me,
  signedOut,
  onClose,
  onSettingsChanged,
  onAccountChanged,
}: SettingsPanelProps) {
  const [settings, setSettings] = useState<LlmSettingsView | null>(null)
  const [profile, setProfile] = useState<ProfileView | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [keyInput, setKeyInput] = useState('')
  const [keyBusy, setKeyBusy] = useState(false)

  const isOwner = me?.role === 'Owner'
  const isActive = me?.isActive === true

  async function loadAll() {
    setError(null)
    try {
      // Every active user owns a profile; the model registry is owner-only.
      setProfile(isActive ? await getProfile() : null)
      if (isOwner) {
        const llm = await getLlmSettings()
        setSettings(llm)
        onSettingsChanged(llm)
      } else {
        setSettings(null)
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load settings')
    }
  }

  useEffect(() => {
    void loadAll()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isOwner, isActive])

  async function handleAssignment(step: string, modelId: string) {
    setError(null)
    try {
      await setLlmAssignment(step, modelId)
      await loadAll()
      setStatus(`Model for ${step} updated.`)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not update the model')
    }
  }

  async function handleProfileSave() {
    if (!profile) return
    setError(null)
    try {
      const saved = await saveProfile(
        profile.experienceSummary,
        profile.goals,
        profile.weeklyHours,
      )
      setProfile(saved)
      setStatus(
        'Profile saved. Existing analyses are now stale and will re-run on the next analysis pass.',
      )
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the profile')
    }
  }

  return (
    <div className="settings-overlay" onClick={onClose}>
      <div className="settings-panel" onClick={(e) => e.stopPropagation()}>
        <div className="settings-header">
          <h2>Settings</h2>
          <button type="button" className="link-button" onClick={onClose}>
            Close
          </button>
        </div>

        {error && <p className="status status-error">{error}</p>}
        {status && <p className="status status-notice">{status}</p>}

        <section>
          <h3>Account</h3>
          {me ? (
            <>
              <p className="settings-hint">
                {me.displayName} · {me.email} · {me.role}
              </p>
              <p className="settings-hint">
                Search &amp; analysis budget: <strong>{formatBudget(me)}</strong>
                {!me.budget.unlimited && (
                  <>
                    {' '}
                    (used ${(me.budget.spentMicros / 1_000_000).toFixed(2)} of $
                    {(me.budget.grantedMicros / 1_000_000).toFixed(2)})
                  </>
                )}
              </p>
              <button
                type="button"
                onClick={() => {
                  // No redirect flow anymore: clear the local session and
                  // reload so every piece of account state resets.
                  void signOut().finally(() => window.location.reload())
                }}
              >
                Sign out
              </button>
            </>
          ) : signedOut ? (
            <p className="settings-hint">Not signed in.</p>
          ) : (
            <p className="settings-hint">Loading account…</p>
          )}
        </section>

        {me?.isActive && (
          <section>
            <h3>Your Anthropic API key</h3>
            <p className="settings-hint">
              Optional. With your own key, searches and analyses bill your Anthropic
              account instead of your free budget here, and premium models like Opus
              unlock. The key is stored encrypted and can only ever be replaced or
              removed — never viewed. The server does use it to call Anthropic on your
              behalf, so we recommend setting a spend limit on the key in the Anthropic
              console.
            </p>
            {me.byoKeyLast4 ? (
              <div className="settings-row">
                <span className="category-code">sk-ant-…{me.byoKeyLast4}</span>
                <button
                  type="button"
                  disabled={keyBusy}
                  onClick={() => {
                    setKeyBusy(true)
                    setError(null)
                    removeAnthropicKey()
                      .then(() => {
                        setStatus('Your API key was removed.')
                        onAccountChanged?.()
                      })
                      .catch((err: unknown) =>
                        setError(err instanceof Error ? err.message : 'Could not remove the key'))
                      .finally(() => setKeyBusy(false))
                  }}
                >
                  Remove
                </button>
              </div>
            ) : (
              <div className="settings-row">
                <input
                  type="password"
                  value={keyInput}
                  onChange={(e) => setKeyInput(e.target.value)}
                  placeholder="sk-ant-…"
                  autoComplete="off"
                />
                <button
                  type="button"
                  disabled={keyBusy || keyInput.trim() === ''}
                  onClick={() => {
                    setKeyBusy(true)
                    setError(null)
                    setAnthropicKey(keyInput.trim())
                      .then(() => {
                        setKeyInput('')
                        setStatus('Key verified and saved. Premium models are now available to you.')
                        onAccountChanged?.()
                      })
                      .catch((err: unknown) =>
                        setError(err instanceof Error ? err.message : 'Could not save the key'))
                      .finally(() => setKeyBusy(false))
                  }}
                >
                  {keyBusy ? 'Verifying…' : 'Save'}
                </button>
              </div>
            )}
          </section>
        )}

        {profile && (
          <section>
            <h3>Your profile</h3>
            <p className="settings-hint">
              Used to personalize analysis and annotate search results. Editing it marks existing
              analyses stale (they re-run on demand — nothing is deleted).
            </p>
            <label>
              Experience
              <textarea
                rows={3}
                value={profile.experienceSummary}
                onChange={(e) =>
                  setProfile({ ...profile, experienceSummary: e.target.value })
                }
                placeholder="e.g. 3 years fullstack (.NET/React), clinical research data pipelines, SQL, Azure"
              />
            </label>
            <label>
              Goals
              <textarea
                rows={3}
                value={profile.goals}
                onChange={(e) => setProfile({ ...profile, goals: e.target.value })}
                placeholder="e.g. move into a machine learning engineering role within a year"
              />
            </label>
            <label>
              Weekly hours for a side project
              <input
                type="number"
                min={0}
                max={100}
                value={profile.weeklyHours ?? ''}
                onChange={(e) =>
                  setProfile({
                    ...profile,
                    weeklyHours: e.target.value === '' ? null : Number(e.target.value),
                  })
                }
              />
            </label>
            <button type="button" onClick={() => void handleProfileSave()}>
              Save profile
            </button>
          </section>
        )}

        {settings && (
          <section>
            <h3>Models per step</h3>
            <p className="settings-hint">
              Bulk steps default to the cheapest tier. Prices shown are per million tokens
              (input / output).
            </p>
            {settings.assignments.map((assignment) => (
              <label key={assignment.step} className="settings-row settings-model-row">
                <span>{STEP_LABELS[assignment.step] ?? assignment.step}</span>
                <select
                  value={assignment.modelId}
                  onChange={(e) => void handleAssignment(assignment.step, e.target.value)}
                >
                  {settings.registry.map((model) => (
                    <option key={model.id} value={model.id}>
                      {model.displayName} (${model.inputPerMTok} / ${model.outputPerMTok})
                    </option>
                  ))}
                </select>
              </label>
            ))}
          </section>
        )}
      </div>
    </div>
  )
}
