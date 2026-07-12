import { useEffect, useState } from 'react'
import {
  createInvite,
  getAdminUsers,
  getInvites,
  grantBudget,
  setUserRole,
} from '../api/client'
import type { AdminUserView, InviteView, MeView } from '../api/types'

function dollars(micros: number): string {
  return `$${(micros / 1_000_000).toFixed(2)}`
}

function when(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  })
}

interface AdminPanelProps {
  me: MeView
}

/**
 * Account operations: find a user, top up their budget, flip their role,
 * mint invite codes. Everything here is audit-logged server-side.
 */
export function AdminPanel({ me }: AdminPanelProps) {
  const [users, setUsers] = useState<AdminUserView[]>([])
  const [invites, setInvites] = useState<InviteView[]>([])
  const [query, setQuery] = useState('')
  const [status, setStatus] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function refresh(search?: string) {
    setError(null)
    try {
      const [u, i] = await Promise.all([getAdminUsers(search), getInvites()])
      setUsers(u)
      setInvites(i)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load accounts')
    }
  }

  useEffect(() => {
    void refresh()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  async function act(label: string, action: () => Promise<unknown>) {
    setBusy(true)
    setError(null)
    setStatus(null)
    try {
      await action()
      setStatus(label)
      await refresh(query || undefined)
    } catch (err) {
      setError(err instanceof Error ? err.message : `${label} failed`)
    } finally {
      setBusy(false)
    }
  }

  function handleGrant(user: AdminUserView) {
    const input = window.prompt(
      `Grant budget to ${user.email} (dollars, e.g. 1 or 2.50):`, '1')
    if (!input) return
    const amount = Number(input)
    if (!Number.isFinite(amount) || amount <= 0 || amount > 1000) {
      setError('Grant must be a dollar amount between 0 and 1000.')
      return
    }
    void act(
      `Granted ${dollars(Math.round(amount * 1_000_000))} to ${user.email}.`,
      () => grantBudget(user.id, Math.round(amount * 1_000_000)),
    )
  }

  function handleRole(user: AdminUserView) {
    const next = user.role === 'Admin' ? 'Member' : 'Admin'
    if (!window.confirm(`Make ${user.email} a ${next}?`)) return
    void act(`${user.email} is now a ${next}.`, () => setUserRole(user.id, next))
  }

  return (
    <div className="admin-panel">
      <section>
        <h2>Accounts</h2>
        <div className="settings-row">
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') void refresh(query || undefined)
            }}
            placeholder="Search by email or name…"
            aria-label="Search accounts"
          />
          <button type="button" disabled={busy} onClick={() => void refresh(query || undefined)}>
            Search
          </button>
        </div>

        {error && <p className="status status-error">{error}</p>}
        {status && <p className="status status-notice">{status}</p>}

        <div className="admin-table-wrap">
          <table className="admin-table">
            <thead>
              <tr>
                <th>User</th>
                <th>Role</th>
                <th>Budget</th>
                <th>Last seen</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.id}>
                  <td>
                    <strong>{u.displayName}</strong>
                    <div className="admin-muted">{u.email}</div>
                    {!u.isActive && <div className="admin-muted">⏳ awaiting invite</div>}
                  </td>
                  <td>{u.role}</td>
                  <td>
                    {u.role === 'Admin'
                      ? '∞'
                      : `${dollars(Math.max(0, u.grantedMicros - u.spentMicros))} left`}
                    <div className="admin-muted">
                      {dollars(u.spentMicros)} of {dollars(u.grantedMicros)} used
                    </div>
                  </td>
                  <td>{when(u.lastSeenUtc)}</td>
                  <td className="admin-actions">
                    <button type="button" disabled={busy} onClick={() => handleGrant(u)}>
                      + Budget
                    </button>
                    <button
                      type="button"
                      disabled={busy || u.id === me.id}
                      title={u.id === me.id ? 'You cannot change your own role' : undefined}
                      onClick={() => handleRole(u)}
                    >
                      {u.role === 'Admin' ? 'Make member' : 'Make admin'}
                    </button>
                  </td>
                </tr>
              ))}
              {users.length === 0 && (
                <tr>
                  <td colSpan={5} className="admin-muted">
                    No accounts match.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>

      <section>
        <h2>Invite codes</h2>
        <p className="settings-hint">
          Only enforced while invite-only signup is enabled (a server setting);
          codes are safe to mint ahead of time.
        </p>
        <button
          type="button"
          disabled={busy}
          onClick={() =>
            void act('Invite code created.', () => createInvite(5))
          }
        >
          New code (5 uses)
        </button>
        <div className="admin-table-wrap">
          <table className="admin-table">
            <thead>
              <tr>
                <th>Code</th>
                <th>Used</th>
                <th>Expires</th>
                <th>Created</th>
              </tr>
            </thead>
            <tbody>
              {invites.map((i) => (
                <tr key={i.id}>
                  <td className="category-code">{i.code}</td>
                  <td>
                    {i.usedCount} / {i.maxUses}
                  </td>
                  <td>{i.expiresUtc ? when(i.expiresUtc) : 'never'}</td>
                  <td>{when(i.createdUtc)}</td>
                </tr>
              ))}
              {invites.length === 0 && (
                <tr>
                  <td colSpan={4} className="admin-muted">
                    No invite codes yet.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  )
}
