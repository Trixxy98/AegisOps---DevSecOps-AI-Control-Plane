# AegisOps user manual — a beginner's guide

This guide is for people who are **new to DevSecOps**. It explains the ideas first, then walks you through
the system step by step. You do not need to know Kubernetes, security scanners or CI pipelines to follow it.
If you already know these topics, the [README](../README.md) and the [design docs](README.md) are shorter.

> **What you need:** a computer with **Docker Desktop** (macOS or Windows) or **Docker Engine** (Linux),
> about **4 GB of free memory** and **5 GB of free disk**. The AI part needs another 4 GB of memory and
> about 2 GB of disk for the model. On Windows, run the commands inside **WSL** (Ubuntu).

---

## 1. What is AegisOps, in plain words?

Imagine a building with a **security guard at the door**. Developers bring new versions of their software to
the door and ask to put them into a room: the *Development* room, the *Staging* room (a rehearsal room), or the
*Production* room (where real customers are). The guard:

- **checks the paperwork**: did the tests pass? did the security scanners find something dangerous?
- **follows written rules** (policies): "nothing with a critical vulnerability goes anywhere",
  "Production needs two people to approve", "no Production changes on Friday night";
- **lets the version in, asks for approvals, or refuses**, and explains why;
- **writes everything in a logbook** (the audit log) so you can always see who did what, and when.

AegisOps is that guard. It does **not** build your code; your CI system (for example GitHub Actions) does that
and then *reports* the result to AegisOps. AegisOps decides whether the result may be deployed, and then
deploys it (as a Docker container, or as a "noop" simulation).

AegisOps also guards a second door: **AI usage**. People can ask a local AI model (through Ollama) for help,
and AegisOps hides secrets in their questions (for example an AWS key), blocks what the policy forbids,
limits how many requests each person can make, and records every request.

## 2. Words you will see (glossary)

| Word | Meaning |
| --- | --- |
| **Project** | One application or service, for example `circuit-service`. |
| **Environment** | A place a project runs: **Development** (for building), **Staging** (rehearsal), **Production** (real users). |
| **Artifact** | One built version of a project, ready to deploy, for example `v1.4.0`. It points to a **container image**. |
| **Container image** | A packaged application (code plus everything it needs) that Docker can run. |
| **Digest** | A unique fingerprint (`sha256:…`) of an image. It guarantees you deploy exactly the bytes that were scanned. |
| **Deployment** | A request to put one artifact into one environment. It is *Requested*, *Evaluated*, then *Succeeded*, *Denied*, *Cancelled* or *Failed*. |
| **Policy** | A named set of rules, for example `Production baseline`. |
| **Rule** | One check inside a policy: "tests must pass", "at most 0 critical findings", "2 approvals", "only Monday–Friday". |
| **Decision** | The result of the rules: **Allow** (go ahead), **RequireApproval** (people must approve first), **Deny** (refused, with a reason). |
| **Approval** | A person with the right role saying "yes" to a deployment. The person who asked can never approve their own request. |
| **Security scan** | An automatic check by a scanner: **Gitleaks** (leaked passwords/keys), **Semgrep** (risky code), **Trivy** (known vulnerabilities in libraries and images). |
| **Finding** | One problem a scanner found, with a severity: Critical, High, Medium, Low. |
| **CVE** | A public ID for a known vulnerability, for example `CVE-2026-31789`. |
| **SARIF** | The standard file format scanners use for their results. |
| **API key** | A secret that lets a machine (like CI) talk to AegisOps for one project. Shown only once. |
| **Audit log** | The logbook of every important action. |
| **Redaction** | Replacing a secret with a placeholder like `[REDACTED:AWS_ACCESS_KEY]` before it reaches the AI model. |

## 3. Install AegisOps on your computer

### 3.1 Get the code and start it

Open a terminal and run these commands one by one. The text after `#` explains each line.

```bash
git clone https://github.com/Trixxy98/AegisOps---DevSecOps-AI-Control-Plane.git aegisops
cd aegisops
scripts/gen-secrets.sh            # creates a private .env file with random keys (keep it secret, never share it)
docker compose up --build -d      # builds and starts everything in the background (about 1 minute)
```

When it finishes, five services are running: the **web** app, the **API**, the **Worker** (does the actual
deployments), **PostgreSQL** (database) and **Redis** (fast messages). Check with:

```bash
docker compose ps                 # every service should say "running" or "healthy"
```

### 3.2 Find your first passwords

The first start creates demo accounts and prints a **temporary password** for each one, **once**, in the API log:

```bash
docker compose logs api | grep 'Development user'
```

| Account | Role | What it is for |
| --- | --- | --- |
| `admin@aegisops.local` | Admin | Everything: users, teams, global policies, audit log, jobs |
| `developer@aegisops.local` | Developer (team Owner) | Requesting deployments, using AI |
| `security@aegisops.local` | Security + Approver | Approving, editing team policies, reviewing findings and AI usage |
| `approver@aegisops.local` | Approver | The second approval for Production |

Copy the passwords somewhere safe. If you lose them, see [Troubleshooting](#8-troubleshooting).

### 3.3 Sign in for the first time

1. Open <http://127.0.0.1:3000> in your browser.
2. Sign in as `admin@aegisops.local` with the temporary password.
3. AegisOps asks you to **choose a new password** (at least 12 characters). This is required for every account
   the first time; until you do it, the system refuses all other actions.
4. You now see the **Overview** page. A green **"Live updates"** dot (top right) means the page updates by itself.

> A yellow **"System degraded — Ollama unavailable"** banner is normal if you have not started the AI part
> (section 6). Everything else still works.

## 4. A tour of the screens

| Menu | What you do there |
| --- | --- |
| **Overview** | Counts of deployments by result (Succeeded, Denied, …). |
| **Projects** | **Create project** (Admins and team Owners). Pick a project to see its **Environments**, create **API keys**, and see its **Artifacts** with a **Deploy …** button per environment. |
| **Deployments** | Every deployment request. Click one to see the **policy decision**, each **rule** that passed or failed, the **timeline** (live), and the **findings** of its artifact. |
| **Approvals** | Requests waiting for *your* approval, with **Approve** / **Reject**. A pop-up (toast) appears here the moment a new one arrives. |
| **Findings** | All security findings across your projects, by severity. Security users can **suppress** a reviewed false positive. |
| **Policies** | The rules. Admin edits Global policies; Security edits team/project policies. Every change creates a new version and an audit entry. |
| **AI Governance** | History of AI requests (what was detected, redacted or blocked) and the AI policy (allowed models, limits, Redact/Block). |
| **AI Playground** | Chat with the local AI model, safely, through the governance rules. |
| **Audit log** | The logbook: who did what, when, with what result. |
| **Administration** | Users (add, activate/deactivate, roles, reset password), teams (members and webhooks) and background jobs (Admin only). |

### Configuration and secrets (Projects screen)

Real applications need settings, such as a database connection string or an API token. Under each environment
in **Projects** there is a **Variables** box:

1. Type a **Variable name** (letters, digits and `_`, for example `ConnectionStrings__Default`; a .NET app
   reads it as `ConnectionStrings:Default`) and a **Value**.
2. Tick **Secret** for passwords and tokens. A secret is stored encrypted and is never shown again, not even to
   an Admin: the list only shows `••••• (secret)`. To change it, save the same name with a new value.
3. Click **Save variable**. The value reaches the container the **next time you deploy** to that environment.

Only an Admin or the team's Owner can add or delete variables; other team members can see the names and the
plain (non-secret) values. `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT` and names starting with `AEGISOPS_`
are set by AegisOps and cannot be used. If saving a secret says **"Secret storage is not configured"**, your
`.env` has no encryption key yet: an `.env` made by an older `scripts/gen-secrets.sh` lacks the
`ENVIRONMENT_SECRETS_*` lines (see `.env.example`). Plain variables still work without it.

### Adding users (Administration screen)

As an Admin, open **Administration → Users**:

1. In **Add user**, type the person's **Email** and **Display name**, tick one or more **Roles** (Developer,
   Security, Approver, Admin) and, if you like, pick a **Team** and whether they are its **Member** or **Owner**.
2. Click **Add user**. A yellow box shows a **temporary password, only once**. Click **Copy** (or select it) and
   give it to the person privately, then click **Dismiss**. At their first sign-in AegisOps makes them choose
   their own password.
3. Each user row has buttons: **Deactivate** / **Activate** (a deactivated user cannot sign in), **Edit roles**
   and **Reset password** (shows a new temporary password once). Role changes apply at the user's next sign-in.

AegisOps protects you from locking yourself out: you cannot deactivate yourself or remove your own Admin role, and
the last active Admin always keeps Admin. A red message explains why when an action is refused.

## 5. Hands-on tutorials (no GitHub account needed)

These tutorials use the seeded project **`circuit-service`** and a saved "CI result" file that ships with
AegisOps, so you can practise without a real CI pipeline. They need `curl` and `jq` (on macOS:
`brew install jq`; on Ubuntu/WSL: `sudo apt install jq`).

### Tutorial 1 — Register your first artifact (and see your first "Deny")

In real life, CI registers artifacts automatically. Here we play the role of CI.

1. Sign in as **developer**. Go to **Projects → circuit-service → API keys**, type a name (for example
   `my-laptop`) and click **Create**. Copy the key that appears — it is shown **only once**.
2. In the terminal (inside the `aegisops` folder):

   ```bash
   export AEGISOPS_URL=http://127.0.0.1:8080
   read -rs "AEGISOPS_API_KEY?Paste the API key: "; echo; export AEGISOPS_API_KEY   # zsh (macOS)
   # bash (Linux/WSL): read -rsp "Paste the API key: " AEGISOPS_API_KEY; echo; export AEGISOPS_API_KEY
   scripts/ci-replay.sh tests/fixtures/ci-payloads/circuit-service.json
   ```

   This sends a saved CI result: version `v0.0.0-ci.replay`, tests passed, and a request to deploy it to **Staging**.
3. In the browser, open **Deployments** and click the newest one. It says **Denied**. Read the rule list:
   the **Staging gate** policy requires fresh Gitleaks, Trivy and Semgrep scans, and this artifact has none.

**What you learned:** AegisOps refuses to put an unscanned version into Staging, and it tells you exactly which
rule failed. Nothing was started.

### Tutorial 2 — Deploy to Development

1. Go to **Projects → circuit-service → Artifacts**. Find `v0.0.0-ci.replay` and click **Deploy Development**.
2. Open **Deployments** and watch the timeline update by itself: *Requested → Policy evaluated → Approved →
   Deploy started → Succeeded*. Development uses the **noop** target, which simulates a deployment.

**What you learned:** the same artifact can be allowed in one environment and denied in another, because each
environment has different rules.

### Tutorial 3 — Production with two approvals

Production needs **two approvals from people other than the requester**. Use two browser sessions so two
people are signed in at once: a normal window and a **private/incognito** window.

1. **Normal window — developer:** Projects → circuit-service → Artifacts → `v0.0.0-ci.replay` → **Deploy Production**.
2. **Private window — security:** sign in as `security@aegisops.local` and open **Approvals**. Within about a
   second a **toast** appears for the new request. Click **Approve**.
3. **Private window:** sign out, sign in as `approver@aegisops.local`, open **Approvals**, click **Approve**.
4. **Normal window:** open the deployment. The timeline shows both approvals, the Worker re-checks the rules,
   and the deployment **Succeeds**.
5. Sign in as **admin** and open **Audit log**: the request, the evaluations, both approvals and the result are
   all there.

If the decision is **Deny**, open the deployment and read which rule failed. For example, if someone added a
**deployment window** rule (see the [demo script](demo-script.md), Journey D), requests outside the allowed hours
are refused with "Outside allowed window".

**What you learned:** risky changes need several people (the "two-person rule"), you cannot approve yourself,
and every step is recorded.

### Tutorial 4 — Change a rule

1. Sign in as **admin** → **Policies** → `Production baseline`.
2. Change the approvals rule from `2` to `1`, save, and see the **version number** increase.
3. Open **Audit log**: there is a `policy.updated` entry with your name.
4. Change it back to `2`.

**What you learned:** rules are data you can change, and every change is versioned and audited. A deployment
always remembers the policy version it was judged by.

## 6. Using the AI features

The AI model runs **on your own computer** (Ollama), so your questions never leave your machine.

1. Start it (the first time downloads the `llama3.2:3b` model, about 2 GB):

   ```bash
   docker compose --profile ai up -d
   ```

2. Wait until the yellow "Ollama unavailable" banner disappears (refresh the page).
3. Open **AI Playground**, choose `llama3.2:3b`, type a question and click **Send**.
4. Try a **fake** secret to see the protection. Type a question that contains `AKIA` immediately followed by
   16 capital letters or digits (for example `EXAMPLEFAKE` and five digits), such as
   *"my deploy failed with key AKIA…, what should I check?"*. It is written in pieces here on purpose: a
   complete example would look like a real AWS key to secret scanners.
   - With the default policy (**Redact**), the model receives `[REDACTED:AWS_ACCESS_KEY]` instead of the key.
   - If an **Admin** switches the Global AI policy to **Block** (AI Governance → AI policy controls; Security can
     do the same for a team policy), the same question is refused before it reaches the model.
5. **AI Governance → AI request history** shows each request, what was detected and redacted, and the outcome —
   never the secret itself.

Other AI helpers: **Explain with AI** on a finding, and **Generate AI summary** on a deployment that waits for
approval. AI answers are labelled *"AI-generated — verify before acting"*: always check them.

> **Never paste real passwords, keys or customer data**, even though AegisOps redacts known patterns.

## 7. Who can do what

| Role | Can | Cannot |
| --- | --- | --- |
| **Developer** | Request deployments for their team's projects, use AI, create API keys if team Owner | Approve, edit policies, see other teams' projects |
| **Security** | See all projects, approve, edit team/project policies, suppress findings, review AI usage | Edit Global policies, manage users |
| **Approver** | Approve or reject deployments of their teams | Request deployments, edit policies |
| **Admin** | Everything, including users, teams, Global policies, audit log and jobs | Approve their own request (nobody can) |

## 8. Troubleshooting

| Problem | What to do |
| --- | --- |
| I lost a first-run password | As admin: `scripts/demo-reset-passwords.sh` gives the demo accounts new temporary passwords. Lost the **admin** password too? `scripts/reset-demo.sh` deletes the demo database and starts fresh (you lose your demo data). |
| "Password change required" / every page fails after sign-in | The account still has a temporary password. Sign out, sign in again and complete the "Choose a new password" step. |
| A port is already in use | AegisOps uses `127.0.0.1` ports 3000 (web), 8080 (API), 5432 (PostgreSQL) and 6380 (Redis). Stop the other program, or change the port in `docker-compose.yml` (Redis: set `AEGISOPS_REDIS_PORT`). |
| "Ollama unavailable" banner | Only the AI features are affected. Start them with `docker compose --profile ai up -d` (section 6). |
| `ci-replay.sh` says HTTP 409 "already exists" | That artifact version is already registered (each version can be registered once). Use the existing one, or copy the JSON file and change `version`. |
| Nothing happens after a request | Check `docker compose ps`: the **worker** must be running. Look at `docker compose logs worker`. |
| Docker Desktop was restarted and things stopped | Run `docker compose up -d` again. Your data is kept. |
| Start completely fresh | `scripts/reset-demo.sh` (asks you to type `reset`; deletes only the demo database and scan reports). |

## 9. Important limits of this version

The setup in this manual is for **learning, evaluation and demos on one computer**: it runs in development mode,
only on `127.0.0.1`, without HTTPS, with demo accounts. To run AegisOps for a team on a server with a real domain
and HTTPS, follow [run AegisOps on a server](deploy-production.md) instead. Even then it is one server (not highly
available), and some security items are still open (see the
[security review](security-review-v1.md) and [roadmap v1.1](roadmap-v1.1.md)).

## 10. Where to go next

- [Demo script](demo-script.md) — the four journeys as a 15-minute presentation.
- [Vision and scope](01-vision-and-scope.md) and [architecture](02-architecture.md) — how the pieces fit together.
- [Policy engine](05-policy-engine.md) — every rule type and how decisions are made.
- [AI gateway](08-ai-gateway.md) — how detection, redaction and limits work.
- [Run AegisOps on a server](deploy-production.md) — production profile, first Admin, HTTPS with Caddy.
- [Operations guide](operations.md) — backups (`scripts/backup.sh`), restores and restore drills
  (`scripts/restore.sh`), upgrades, rotating keys and passwords, monitoring and sizing. Back up before you
  upgrade, also on a laptop.
- [Connect your repository](connect-your-repository.md) — put **your own application** behind AegisOps: create
  the project, configure Staging, add the copy-paste GitHub Actions workflow (or plain `curl` for GitLab, Jenkins
  and others), and fix common problems.
- Found a bug? Open an issue. Found a security problem? Follow [SECURITY.md](../SECURITY.md).
