import { useEffect, useState, type FormEvent } from "react";
import { api, ApiError } from "./api";

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
    <section className="panel" aria-label="Users">
      <div className="panel-heading">
        <h2>Users</h2>
        <span>{users.length} total</span>
      </div>

      {notice && (
        <div className="key-notice" role="status" aria-label="Temporary password">
          <p>
            {notice.action} {notice.email}. Temporary password (shown only once):{" "}
            <code data-testid="temporary-password">{notice.password}</code>
          </p>
          <p>
            Copy it now and give it to the user over a private channel; it cannot be shown again. They must choose a new
            password at first sign-in.
          </p>
          <div className="action-row">
            <button className="secondary-button" type="button" onClick={() => void copyPassword()}>
              Copy
            </button>
            <button className="secondary-button" type="button" onClick={() => setNotice(null)}>
              Dismiss
            </button>
          </div>
        </div>
      )}

      {listError && (
        <p role="alert" className="inline-error">
          {listError}
        </p>
      )}

      <ul className="item-list">
        {users.map((user) => (
          <li key={user.id} className="detail-row" aria-label={user.email}>
            <span>
              {user.displayName} · {user.email} · {user.roles.join(", ") || "no roles"}
              {!user.isActive && " · inactive"}
            </span>
            {editing?.id === user.id ? (
              <fieldset className="webhook-events" aria-label={`Roles for ${user.email}`}>
                <legend>Roles</legend>
                {allRoles.map((role) => (
                  <label key={role} className="checkbox-label">
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
                <div className="action-row">
                  <button
                    className="primary-button"
                    type="button"
                    disabled={busy === user.id || editing.roles.length === 0}
                    onClick={() => void saveRoles()}
                  >
                    Save roles
                  </button>
                  <button className="secondary-button" type="button" onClick={() => setEditing(null)}>
                    Cancel
                  </button>
                </div>
              </fieldset>
            ) : (
              <div className="action-row">
                {canManageUsers && user.id !== currentUserId && (
                  <button
                    className={user.isActive ? "danger-button" : "secondary-button"}
                    type="button"
                    disabled={busy === user.id}
                    onClick={() => void setActive(user, !user.isActive)}
                  >
                    {user.isActive ? "Deactivate" : "Activate"}
                  </button>
                )}
                {canManageRoles && (
                  <button
                    className="secondary-button"
                    type="button"
                    disabled={busy === user.id}
                    onClick={() => setEditing({ id: user.id, roles: [...user.roles] })}
                  >
                    Edit roles
                  </button>
                )}
                {canManageUsers && (
                  <button
                    className="secondary-button"
                    type="button"
                    disabled={busy === user.id}
                    onClick={() => void resetPassword(user)}
                  >
                    Reset password
                  </button>
                )}
              </div>
            )}
            {rowErrors[user.id] && (
              <p role="alert" className="inline-error">
                {rowErrors[user.id]}
              </p>
            )}
          </li>
        ))}
      </ul>

      {canManageUsers && (
        <form className="create-project-form" aria-label="Add user" onSubmit={(event) => void createUser(event)}>
          <p className="environment-title">Add user</p>
          <label className="field-label">
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
          <label className="field-label">
            Display name
            <input
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
              required
              maxLength={100}
            />
          </label>
          <fieldset className="webhook-events">
            <legend>Roles</legend>
            {allRoles.map((role) => (
              <label key={role} className="checkbox-label">
                <input
                  type="checkbox"
                  checked={roles.includes(role)}
                  onChange={(event) => setRoles((current) => toggle(current, role, event.target.checked))}
                />
                {role}
              </label>
            ))}
          </fieldset>
          <label className="field-label">
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
            <label className="field-label">
              Team role
              <select value={teamRole} onChange={(event) => setTeamRole(event.target.value)}>
                <option value="Member">Member</option>
                <option value="Owner">Owner</option>
              </select>
            </label>
          )}
          <p className="field-hint">
            AegisOps generates a temporary password and shows it once. The user must change it at first sign-in.
          </p>
          <button
            className="primary-button"
            type="submit"
            disabled={creating || email.trim() === "" || displayName.trim() === "" || roles.length === 0}
          >
            Add user
          </button>
          {createError && (
            <p role="alert" className="inline-error">
              {createError}
            </p>
          )}
        </form>
      )}
    </section>
  );
}
