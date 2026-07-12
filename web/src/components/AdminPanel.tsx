import { useEffect, useState } from 'react'
import {
  createInvite,
  getAdminUsers,
  getInvites,
  grantBudget,
  setUserRole,
} from '../api/client'
import type { AdminUserView, InviteView, MeView, PagedResult, Role } from '../api/types'
import { Pagination } from './Pagination'

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
 * Account operations, built to scale past the friends era: paged account
 * table with search, invite management on its own tab, themed dialogs (no
 * browser prompts). Role changes render only for Owners — the API enforces
 * the same split.
 */
export function AdminPanel({ me }: AdminPanelProps) {
  const isOwner = me.role === 'Owner'
  const [tab, setTab] = useState<'accounts' | 'invites'>('accounts')
  const [users, setUsers] = useState<PagedResult<AdminUserView> | null>(null)
  const [invites, setInvites] = useState<InviteView[]>([])
  const [query, setQuery] = useState('')
  const [activeQuery, setActiveQuery] = useState<string | undefined>(undefined)
  const [page, setPage] = useState(1)
  const [status, setStatus] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [grantTarget, setGrantTarget] = useState<AdminUserView | null>(null)
  const [roleTarget, setRoleTarget] = useState<AdminUserView | null>(null)

  async function loadUsers(nextPage = page, nextQuery = activeQuery) {
    setError(null)
    try {
      setUsers(await getAdminUsers(nextQuery, nextPage))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load accounts')
    }
  }

  async function loadInvites() {
    setError(null)
    try {
      setInvites(await getInvites())
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load invites')
    }
  }

  useEffect(() => {
    void loadUsers(1, undefined)
    void loadInvites()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  function search() {
    const next = query.trim() === '' ? undefined : query.trim()
    setActiveQuery(next)
    setPage(1)
    void loadUsers(1, next)
  }

  function changePage(next: number) {
    setPage(next)
    void loadUsers(next)
  }

  async function act(label: string, action: () => Promise<unknown>) {
    setBusy(true)
    setError(null)
    setStatus(null)
    try {
      await action()
      setStatus(label)
      await Promise.all([loadUsers(), loadInvites()])
    } catch (err) {
      setError(err instanceof Error ? err.message : `${label} failed`)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="admin-panel">
      <nav className="admin-tabs">
        <button
          type="button"
          className={tab === 'accounts' ? 'tab tab-active' : 'tab'}
          onClick={() => setTab('accounts')}
        >
          Accounts{users ? ` (${users.totalItems})` : ''}
        </button>
        <button
          type="button"
          className={tab === 'invites' ? 'tab tab-active' : 'tab'}
          onClick={() => setTab('invites')}
        >
          Invite codes{invites.length > 0 ? ` (${invites.length})` : ''}
        </button>
      </nav>

      {error && <p className="status status-error">{error}</p>}
      {status && <p className="status status-notice">{status}</p>}

      {tab === 'accounts' && (
        <section>
          <div className="admin-search">
            <input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') search()
              }}
              placeholder="Search by email or name…"
              aria-label="Search accounts"
            />
            <button type="button" className="primary-button" disabled={busy} onClick={search}>
              Search
            </button>
            {activeQuery && (
              <button
                type="button"
                className="link-button"
                onClick={() => {
                  setQuery('')
                  setActiveQuery(undefined)
                  setPage(1)
                  void loadUsers(1, undefined)
                }}
              >
                Clear
              </button>
            )}
          </div>

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
                {(users?.items ?? []).map((u) => (
                  <tr key={u.id}>
                    <td>
                      <strong>{u.displayName}</strong>
                      <div className="admin-muted">{u.email}</div>
                      {!u.isActive && <div className="admin-muted">⏳ awaiting invite</div>}
                    </td>
                    <td>{u.role}</td>
                    <td>
                      {u.role !== 'Member' ? (
                        '∞'
                      ) : (
                        <>
                          {dollars(Math.max(0, u.grantedMicros - u.spentMicros))} left
                          <div className="admin-muted">
                            {dollars(u.spentMicros)} of {dollars(u.grantedMicros)} used
                          </div>
                        </>
                      )}
                    </td>
                    <td>{when(u.lastSeenUtc)}</td>
                    <td className="admin-actions">
                      <div className="admin-actions-group">
                        <button type="button" disabled={busy} onClick={() => setGrantTarget(u)}>
                          + Budget
                        </button>
                        {isOwner && u.id !== me.id && (
                          <button type="button" disabled={busy} onClick={() => setRoleTarget(u)}>
                            Change role
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
                {users !== null && users.items.length === 0 && (
                  <tr>
                    <td colSpan={5} className="admin-muted">
                      No accounts match.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>

          {users && (
            <Pagination
              page={users.page}
              totalPages={users.totalPages}
              totalItems={users.totalItems}
              onPageChange={changePage}
              itemsLabel="accounts"
            />
          )}
        </section>
      )}

      {tab === 'invites' && (
        <section>
          <p className="settings-hint">
            Only enforced while invite-only signup is enabled (a server setting); codes are
            safe to mint ahead of time.
          </p>
          <button
            type="button"
            className="primary-button"
            disabled={busy}
            onClick={() => void act('Invite code created.', () => createInvite(5))}
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
      )}

      {grantTarget && (
        <GrantDialog
          user={grantTarget}
          busy={busy}
          onClose={() => setGrantTarget(null)}
          onGrant={(amountMicros) => {
            setGrantTarget(null)
            void act(
              `Granted ${dollars(amountMicros)} to ${grantTarget.email}.`,
              () => grantBudget(grantTarget.id, amountMicros),
            )
          }}
        />
      )}

      {roleTarget && (
        <RoleDialog
          user={roleTarget}
          busy={busy}
          onClose={() => setRoleTarget(null)}
          onSelect={(role) => {
            setRoleTarget(null)
            void act(
              `${roleTarget.email} is now ${role === 'Member' ? 'a Member' : `an ${role}`}.`,
              () => setUserRole(roleTarget.id, role),
            )
          }}
        />
      )}
    </div>
  )
}

function GrantDialog({
  user,
  busy,
  onClose,
  onGrant,
}: {
  user: AdminUserView
  busy: boolean
  onClose: () => void
  onGrant: (amountMicros: number) => void
}) {
  const [amount, setAmount] = useState('1')
  const parsed = Number(amount)
  const valid = Number.isFinite(parsed) && parsed > 0 && parsed <= 1000

  return (
    <div className="settings-overlay auth-overlay" onClick={onClose}>
      <div className="admin-dialog" onClick={(e) => e.stopPropagation()}>
        <h3>Grant budget</h3>
        <p className="settings-hint">
          Top up <strong>{user.email}</strong> — they currently have{' '}
          {dollars(Math.max(0, user.grantedMicros - user.spentMicros))} left.
        </p>
        <form
          onSubmit={(e) => {
            e.preventDefault()
            if (valid) onGrant(Math.round(parsed * 1_000_000))
          }}
        >
          <label>
            Amount (dollars)
            <input
              inputMode="decimal"
              autoFocus
              value={amount}
              onChange={(e) => setAmount(e.target.value)}
              placeholder="1.00"
            />
          </label>
          <div className="admin-dialog-actions">
            <button type="button" className="link-button" onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className="primary-button" disabled={busy || !valid}>
              Grant {valid ? dollars(Math.round(parsed * 1_000_000)) : ''}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}

function RoleDialog({
  user,
  busy,
  onClose,
  onSelect,
}: {
  user: AdminUserView
  busy: boolean
  onClose: () => void
  onSelect: (role: Role) => void
}) {
  const roles: { role: Role; blurb: string }[] = [
    { role: 'Member', blurb: 'Normal account with a metered budget.' },
    { role: 'Admin', blurb: 'People ops: accounts, budget grants, invite codes. Unlimited personal budget.' },
    { role: 'Owner', blurb: 'Everything, including roles, model settings, and ingestion. Co-owner trust.' },
  ]

  return (
    <div className="settings-overlay auth-overlay" onClick={onClose}>
      <div className="admin-dialog" onClick={(e) => e.stopPropagation()}>
        <h3>Change role</h3>
        <p className="settings-hint">
          <strong>{user.email}</strong> is currently <strong>{user.role}</strong>.
        </p>
        <div className="admin-role-options">
          {roles.map(({ role, blurb }) => (
            <button
              key={role}
              type="button"
              disabled={busy || role === user.role}
              onClick={() => onSelect(role)}
            >
              <strong>{role}</strong>
              <span>{blurb}</span>
            </button>
          ))}
        </div>
        <div className="admin-dialog-actions">
          <button type="button" className="link-button" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  )
}
