import { useEffect, useState, type FormEvent } from "react";
import { api, ApiError } from "./api";
import { Alert } from "./ui";

/** GET /api/v1/teams (ListTeams.cs). `myRole` is null for Admins, who see every team. */
type Team = { id: string; name: string; slug: string; description?: string | null; myRole?: string | null };

/** 201 body of POST /api/v1/projects (CreateProject.cs, ProjectResponse); only the fields the page uses. */
export type CreatedProject = { id: string; teamId: string; name: string; slug: string };

function problemMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return [error.problem.title ?? error.message, error.problem.detail].filter(Boolean).join(" ");
  }
  return error instanceof Error ? error.message : "Request failed.";
}

/** Turns a project name into a suggested slug: lowercase letters, digits and hyphens. */
function suggestSlug(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 100);
}

/**
 * "Create project" form for the Projects page. The API allows Admins and the Owner of the chosen team
 * (projects:write narrowed in the handler), so the team list only offers teams the caller owns.
 * New projects get Development, Staging and Production environments with a noop target.
 */
export function CreateProject({ token, onCreated }: { token: string; onCreated: (project: CreatedProject) => void }) {
  const [teams, setTeams] = useState<Team[] | null>(null);
  const [teamId, setTeamId] = useState("");
  const [name, setName] = useState("");
  const [slug, setSlug] = useState("");
  const [slugEdited, setSlugEdited] = useState(false);
  const [description, setDescription] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let active = true;
    api<Team[]>("/api/v1/teams", token)
      .then((items) => {
        if (!active) return;
        const owned = items.filter((team) => team.myRole == null || team.myRole === "Owner");
        setTeams(owned);
        setTeamId((current) => current || owned[0]?.id || "");
      })
      .catch((reason: unknown) => {
        if (active) {
          setTeams([]);
          setError(problemMessage(reason));
        }
      });
    return () => {
      active = false;
    };
  }, [token]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaving(true);
    setError(null);
    try {
      const created = await api<CreatedProject>("/api/v1/projects", token, {
        method: "POST",
        body: JSON.stringify({
          teamId,
          name: name.trim(),
          slug: slug.trim(),
          description: description.trim() === "" ? null : description.trim(),
        }),
      });
      setName("");
      setSlug("");
      setSlugEdited(false);
      setDescription("");
      onCreated(created);
    } catch (reason) {
      setError(problemMessage(reason));
    } finally {
      setSaving(false);
    }
  }

  if (teams !== null && teams.length === 0 && error === null) {
    return (
      <p className="field-hint">
        To create a project you must be an Owner of a team. Ask an Admin to make you one (Administration → Teams).
      </p>
    );
  }

  return (
    <form className="form" aria-label="Create project" onSubmit={(event) => void submit(event)}>
      <label className="field">
        Team
        <select value={teamId} onChange={(event) => setTeamId(event.target.value)} required>
          {(teams ?? []).map((team) => (
            <option key={team.id} value={team.id}>
              {team.name}
            </option>
          ))}
        </select>
      </label>
      <label className="field">
        Name
        <input
          value={name}
          onChange={(event) => {
            setName(event.target.value);
            if (!slugEdited) setSlug(suggestSlug(event.target.value));
          }}
          placeholder="Orders service"
          required
          maxLength={200}
        />
      </label>
      <div className="field">
        <label htmlFor="create-project-slug">Slug</label>
        <input
          id="create-project-slug"
          value={slug}
          onChange={(event) => {
            setSlug(event.target.value);
            setSlugEdited(true);
          }}
          placeholder="orders-service"
          required
          maxLength={100}
          aria-describedby="create-project-slug-hint"
        />
        <p id="create-project-slug-hint" className="field-hint">
          Lowercase letters, digits and hyphens. CI uses it in API calls; it cannot be changed later.
        </p>
      </div>
      <label className="field">
        Description (optional)
        <input value={description} onChange={(event) => setDescription(event.target.value)} maxLength={2000} />
      </label>
      {error && <Alert>{error}</Alert>}
      <div className="form-actions">
        <button
          className="btn btn-primary"
          type="submit"
          disabled={saving || teamId === "" || name.trim() === "" || slug.trim() === ""}
        >
          Create project
        </button>
      </div>
    </form>
  );
}
