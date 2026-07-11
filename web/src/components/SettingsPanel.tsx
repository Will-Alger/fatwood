import { useEffect, useState } from 'react'
import {
  getAdminKey,
  getLlmSettings,
  getProfile,
  saveProfile,
  setAdminKey,
  setLlmAssignment,
} from '../api/client'
import type { LlmSettingsView, ProfileView } from '../api/types'

const STEP_LABELS: Record<string, string> = {
  QueryCompiler: 'Query compiler (runs once per search)',
  PaperAnalysis: 'Paper analysis (runs once per paper)',
}

interface SettingsPanelProps {
  onClose: () => void
  onSettingsChanged: (settings: LlmSettingsView | null) => void
}

export function SettingsPanel({ onClose, onSettingsChanged }: SettingsPanelProps) {
  const [adminKey, setAdminKeyState] = useState(getAdminKey())
  const [settings, setSettings] = useState<LlmSettingsView | null>(null)
  const [profile, setProfile] = useState<ProfileView | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function loadAll() {
    setError(null)
    if (getAdminKey() === '') {
      setSettings(null)
      setProfile(null)
      return
    }
    try {
      const [llm, prof] = await Promise.all([getLlmSettings(), getProfile()])
      setSettings(llm)
      setProfile(prof)
      onSettingsChanged(llm)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load settings')
    }
  }

  useEffect(() => {
    void loadAll()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  function handleKeySave() {
    setAdminKey(adminKey.trim())
    setStatus('Admin key saved locally.')
    void loadAll()
  }

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
          <h3>Admin API key</h3>
          <p className="settings-hint">
            Required for smart search, analysis, profile, and model settings. Stored only in this
            browser.
          </p>
          <div className="settings-row">
            <input
              type="password"
              value={adminKey}
              onChange={(e) => setAdminKeyState(e.target.value)}
              placeholder="X-Admin-Api-Key value"
            />
            <button type="button" onClick={handleKeySave}>
              Save
            </button>
          </div>
        </section>

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
