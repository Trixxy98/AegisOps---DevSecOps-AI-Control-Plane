import { useEffect, useState, type FormEvent } from "react";
import { api, ApiError } from "./api";
import { IconAlert, IconCopy } from "./icons";
import { Alert, CardHeader, StatusBadge } from "./ui";

/** GET /api/v1/admin/users and the PATCH/PUT responses (UserListItem in ListUsers.cs). */
export type ManagedUser = { id: string; email: string; displayName: string; isActive: boolean; roles: string[] };

/** 201 body of POST /api/v1/admin/users (CreateUserResponse in CreateUser.cs). */
type CreatedUser = {
  id: string;
  email: string;
  displayName: string;
  roles: string[];
  team: { teamId: string; slug: string; role: string } | null;
  temporaryPassword: string;
  mustChangePassword: boolean;
};

/** 200 body of POST /api/v1/admin/users/{id}/reset-password (ResetUserPasswordResponse). */
type ResetPassword = { userId: string; temporaryPassword: string; mustChangePassword: boolean };

const allRoles = ["Admin", "Developer", "Security", "Approver"] as const;

function problemMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return [error.problem.title ?? error.message, error.problem.detail].filter(Boolean).join(" ");
  }
  return error instanceof Error ? error.message : "Request failed.";
}

/**
 * Administration → Users (docs/09 §3 user lifecycle). Admins (users:manage) add users with a temporary password that
 * is shown once, activate/deactivate them and reset passwords; changing roles needs roles:manage. The API enforces
 * the guard rails (no self-deactivation, the last active Admin keeps Admin) and the page shows its Problem Details.
 */
export function UserManagement({
  token,
  permissions,
  currentUserId,
  teams,
}: {
  token: string;
  permissions: string[];
  currentUserId: string | null;
  teams: Array<{ slug: string; name: string }>;
}) {
  const canManageUsers = permissions.includes("users:manage");
  const canManageRoles = permissions.includes("roles:manage");
  const [users, setUsers] = useState<ManagedUser[]>([]);
  const [listError, setListError] = useState<string | null>(null);
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [roles, setRoles] = useState<string[]>(["Developer"]);
  const [teamSlug, setTeamSlug] = useState("");
  const [teamRole, setTeamRole] = useState("Member");
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ email: string; password: string; action: string } | null>(null);
  const [rowErrors, setRowErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [editing, setEditing] = useState<{ id: string; roles: string[] } | null>(null);

  useEffect(() => {
    let active = true;
    api<ManagedUser[]>("/api/v1/admin/users", token)
      .then((items) => {
        if (active) setUsers(items);
      })
      .catch((reason: unknown) => {
        if (active) setListError(problemMessage(reason));
      });
    return () => {
      active = false;
    };
  }, [token]);

  function replaceUser(updated: ManagedUser) {
    setUsers((current) => current.map((item) => (item.id === updated.id ? updated : item)));
  }

  function setRowError(id: string, message: string | null) {
    setRowErrors((current) => {
      const next = { ...current };
      if (message) next[id] = message;
      else delete next[id];
      return next;
    });
  }

  async function createUser(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setCreating(true);
    setCreateError(null);
    try {
      const created = await api<CreatedUser>("/api/v1/admin/users", token, {
        method: "POST",
        body: JSON.stringify({
          email: email.trim(),
          displayName: displayName.trim(),
          roles,
          team: teamSlug ? { slug: teamSlug, role: teamRole } : null,
        }),
      });
      setUsers((current) =>
        [
          ...current,
          {
            id: created.id,
            email: created.email,
            displayName: created.displayName,
            isActive: true,
            roles: created.roles,
          },
        ].sort((left, right) => left.email.localeCompare(right.email)),
      );
      setNotice({ email: created.email, password: created.temporaryPassword, action: "Created" });
      setEmail("");
      setDisplayName("");
      setRoles(["Developer"]);
      setTeamSlug("");
      setTeamRole("Member");
    } catch (reason) {
      setCreateError(problemMessage(reason));
    } finally {
      setCreating(false);
    }
  }

  async function setActive(user: ManagedUser, isActive: boolean) {
    setBusy(user.id);
    setRowError(user.id, null);
    try {
      replaceUser(
        await api<ManagedUser>(`/api/v1/admin/users/${user.id}`, token, {
          method: "PATCH",
          body: JSON.stringify({ isActive }),
        }),
      );
    } catch (reason) {
      setRowError(user.id, problemMessage(reason));
    } finally {
      setBusy(null);
    }
  }

  async function saveRoles() {
    if (!editing) return;
    const id = editing.id;
    setBusy(id);
    setRowError(id, null);
    try {
      replaceUser(
        await api<ManagedUser>(`/api/v1/admin/users/${id}/roles`, token, {
          method: "PUT",
          body: JSON.stringify({ roles: editing.roles }),
        }),
      );
      setEditing(null);
    } catch (reason) {
      setRowError(id, problemMessage(reason));
    } finally {
      setBusy(null);
    }
  }

  async function resetPassword(user: ManagedUser) {
    setBusy(user.id);
    setRowError(user.id, null);
    try {
      const reset = await api<ResetPassword>(`/api/v1/admin/users/${user.id}/reset-password`, token, {
        method: "POST",
      });
      setNotice({ email: user.email, password: reset.temporaryPassword, action: "Reset the password of" });
    } catch (reason) {
      setRowError(user.id, problemMessage(reason));
    } finally {
      setBusy(null);
    }
  }

  async function copyPassword() {
    if (!notice) return;
    try {
      await navigator.clipboard.writeText(notice.password);
    } catch {
      // Clipboard access can be denied; the password stays visible so it can be copied by hand.
    }
  }

  function toggle(list: string[], role: string, checked: boolean) {
    return checked ? [...list.filter((item) => item !== role), role] : list.filter((item) => item !== role);
  }

  return (
    <div className="split split-aside">
      <section className="card" aria-label="Users">
        <CardHeader title="Users" meta={users.length} />

        {notice && (
          <div className="card-body">
            <div className="alert alert-warning secret-banner" role="status" aria-label="Temporary password">
              <IconAlert className="alert-icon" />
              <div className="alert-body">
                <p>
                  <strong className="alert-title">
                    {notice.action} {notice.email}.
                  </strong>{" "}
                  Temporary password (shown only once):
                </p>
                <code className="secret-value" data-testid="temporary-password">
                  {notice.password}
                </code>
                <p className="muted">
                  Copy it now and give it to the user over a private channel; it cannot be shown again. They must choose
                  a new password at first sign-in.
                </p>
                <div className="form-actions">
                  <button className="btn btn-secondary btn-sm" type="button" onClick={() => void copyPassword()}>
                    <IconCopy size={14} />
                    Copy
                  </button>
                  <button className="btn btn-ghost btn-sm" type="button" onClick={() => setNotice(null)}>
                    Dismiss
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}

        {listError && (
          <div className="card-body">
            <Alert>{listError}</Alert>
          </div>
        )}

        <ul className="list">
          {users.map((user) => (
            <li key={user.id} className="user-row" aria-label={user.email}>
              <div className="user-card">
                <span className="avatar" aria-hidden="true">
                  {(user.displayName || user.email).slice(0, 1).toUpperCase()}
                </span>
                <span className="user-text">
                  <strong>
                    {user.displayName}
                    {user.id === currentUserId && <span className="subtle"> (you)</span>}
                  </strong>
                  <span>{user.email}</span>
                </span>
              </div>
              <div className="row user-meta">
                <span className="muted user-roles">{user.roles.join(", ") || "No roles"}</span>
                {!user.isActive && <StatusBadge status="Inactive" tone="neutral" />}
              </div>
              {editing?.id === user.id ? (
                <fieldset className="checkbox-group role-editor" aria-label={`Roles for ${user.email}`}>
                  <legend>Roles</legend>
                  <div className="checkbox-grid">
                    {allRoles.map((role) => (
                      <label key={role} className="checkbox">
                        <input
                          type="checkbox"
                          checked={editing.roles.includes(role)}
                          onChange={(event) =>
                            setEditing({ id: user.id, roles: toggle(editing.roles, role, event.target.checked) })
                          }
                        />
                        {role}
                      </label>
                    ))}
                  </div>
                  <div className="form-actions">
                    <button
                      className="btn btn-primary btn-sm"
                      type="button"
                      disabled={busy === user.id || editing.roles.length === 0}
                      onClick={() => void saveRoles()}
                    >
                      Save roles
                    </button>
                    <button className="btn btn-ghost btn-sm" type="button" onClick={() => setEditing(null)}>
                      Cancel
                    </button>
                  </div>
                </fieldset>
              ) : (
                <div className="row user-actions">
                  {canManageRoles && (
                    <button
                      className="btn btn-secondary btn-sm"
                      type="button"
                      disabled={busy === user.id}
                      onClick={() => setEditing({ id: user.id, roles: [...user.roles] })}
                    >
                      Edit roles
                    </button>
                  )}
                  {canManageUsers && (
                    <button
                      className="btn btn-secondary btn-sm"
                      type="button"
                      disabled={busy === user.id}
                      onClick={() => void resetPassword(user)}
                    >
                      Reset password
                    </button>
                  )}
                  {canManageUsers && user.id !== currentUserId && (
                    <button
                      className={`btn btn-sm ${user.isActive ? "btn-danger" : "btn-secondary"}`}
                      type="button"
                      disabled={busy === user.id}
                      onClick={() => void setActive(user, !user.isActive)}
                    >
                      {user.isActive ? "Deactivate" : "Activate"}
                    </button>
                  )}
                </div>
              )}
              {rowErrors[user.id] && <Alert>{rowErrors[user.id]}</Alert>}
            </li>
          ))}
        </ul>
      </section>

      {canManageUsers && (
        <section className="card">
          <CardHeader title="Add user" />
          <form className="card-body form" aria-label="Add user" onSubmit={(event) => void createUser(event)}>
            <label className="field">
              Email
              <input
                type="email"
                value={email}
                onChange={(event) => setEmail(event.target.value)}
                placeholder="name@example.com"
                autoComplete="off"
                required
                maxLength={256}
              />
            </label>
            <label className="field">
              Display name
              <input
                value={displayName}
                onChange={(event) => setDisplayName(event.target.value)}
                required
                maxLength={100}
              />
            </label>
            <fieldset className="checkbox-group">
              <legend>Roles</legend>
              <div className="checkbox-grid">
                {allRoles.map((role) => (
                  <label key={role} className="checkbox">
                    <input
                      type="checkbox"
                      checked={roles.includes(role)}
                      onChange={(event) => setRoles((current) => toggle(current, role, event.target.checked))}
                    />
                    {role}
                  </label>
                ))}
              </div>
            </fieldset>
            <label className="field">
              Team (optional)
              <select value={teamSlug} onChange={(event) => setTeamSlug(event.target.value)}>
                <option value="">No team</option>
                {teams.map((team) => (
                  <option key={team.slug} value={team.slug}>
                    {team.name}
                  </option>
                ))}
              </select>
            </label>
            {teamSlug && (
              <label className="field">
                Team role
                <select value={teamRole} onChange={(event) => setTeamRole(event.target.value)}>
                  <option value="Member">Member</option>
                  <option value="Owner">Owner</option>
                </select>
              </label>
            )}
            <p className="field-hint">A temporary password is shown once; the user changes it at first sign-in.</p>
            {createError && <Alert>{createError}</Alert>}
            <div className="form-actions">
              <button
                className="btn btn-primary"
                type="submit"
                disabled={creating || email.trim() === "" || displayName.trim() === "" || roles.length === 0}
              >
                Add user
              </button>
            </div>
          </form>
        </section>
      )}
    </div>
  );
}
