import { useState } from "react";
import { api } from "./api";
import { LogoMark } from "./icons";
import { Alert } from "./ui";

export function Login({ onToken }: { onToken: (token: string) => void }) {
  const [email, setEmail] = useState("admin@aegisops.local");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Set when the account still has a temporary password (docs/09): the workspace opens only after it is changed.
  const [pendingToken, setPendingToken] = useState<string | null>(null);
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");

  async function login(secret: string) {
    const response = await fetch("/api/v1/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, password: secret }),
    });
    const body = (await response.json().catch(() => ({}))) as { title?: string; accessToken?: string };
    if (!response.ok || !body.accessToken) throw new Error(body.title ?? "Login failed");
    return body.accessToken;
  }

  async function signIn() {
    setError(null);
    setBusy(true);
    try {
      const token = await login(password);
      // If /auth/me cannot be read, continue as before: the API still refuses temporary-password tokens.
      const me = await api<{ mustChangePassword?: boolean }>("/api/v1/auth/me", token).catch(() => null);
      if (me?.mustChangePassword) {
        setPendingToken(token);
        return;
      }
      onToken(token);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Login failed");
    } finally {
      setBusy(false);
    }
  }

  async function changePassword() {
    setError(null);
    if (newPassword.length < 12) {
      setError("The new password must be at least 12 characters.");
      return;
    }
    if (newPassword !== confirmPassword) {
      setError("The new passwords do not match.");
      return;
    }
    if (newPassword === password) {
      setError("Choose a password different from the temporary one.");
      return;
    }
    setBusy(true);
    try {
      await api("/api/v1/auth/change-password", pendingToken!, {
        method: "POST",
        body: JSON.stringify({ currentPassword: password, newPassword }),
      });
      // A fresh token no longer carries the password-change requirement.
      onToken(await login(newPassword));
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "Password change failed");
    } finally {
      setBusy(false);
    }
  }

  if (pendingToken) {
    return (
      <main className="auth-page">
        <div className="auth-panel">
          <div className="auth-brand">
            <LogoMark size={36} />
            <div>
              <h1>Choose a new password</h1>
              <p>Replace your temporary password to continue.</p>
            </div>
          </div>
          <form
            className="auth-card"
            onSubmit={(event) => {
              event.preventDefault();
              void changePassword();
            }}
          >
            {error && <Alert>{error}</Alert>}
            <div className="field">
              <label htmlFor="new-password">New password</label>
              <input
                id="new-password"
                autoComplete="new-password"
                type="password"
                aria-describedby="new-password-hint"
                value={newPassword}
                onChange={(event) => setNewPassword(event.target.value)}
              />
              <span id="new-password-hint" className="field-hint">
                At least 12 characters.
              </span>
            </div>
            <label className="field" htmlFor="confirm-password">
              Confirm new password
              <input
                id="confirm-password"
                autoComplete="new-password"
                type="password"
                value={confirmPassword}
                onChange={(event) => setConfirmPassword(event.target.value)}
              />
            </label>
            <button className="btn btn-primary" type="submit" disabled={busy}>
              Set password and continue
            </button>
          </form>
        </div>
      </main>
    );
  }

  return (
    <main className="auth-page">
      <div className="auth-panel">
        <div className="auth-brand">
          <LogoMark size={36} />
          <h1>Sign in to AegisOps</h1>
        </div>
        <form
          className="auth-card"
          onSubmit={(event) => {
            event.preventDefault();
            void signIn();
          }}
        >
          {error && <Alert>{error}</Alert>}
          <label className="field" htmlFor="login-email">
            Email
            <input
              id="login-email"
              autoComplete="username"
              type="email"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
            />
          </label>
          <label className="field" htmlFor="login-password">
            Password
            <input
              id="login-password"
              autoComplete="current-password"
              type="password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
            />
          </label>
          <button className="btn btn-primary" type="submit" disabled={busy}>
            Continue
          </button>
        </form>
      </div>
    </main>
  );
}
