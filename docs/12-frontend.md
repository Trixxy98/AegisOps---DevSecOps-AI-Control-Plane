# 12 — Frontend (AegisOps.Web)

React 19 · TypeScript · Vite · Tailwind CSS 4 · TanStack Query 5 · React Router 7 · Axios ·
`@microsoft/signalr` · react-hook-form + zod · recharts · lucide-react.

## 1. Principles

1. **The API decides; the UI displays.** No business rules in the client. Permissions from
   `/auth/me` only hide/disable controls.
2. **Server state lives in TanStack Query.** Component state is for forms and UI toggles.
3. **Real-time = invalidate, don't patch.** SignalR messages trigger
   `invalidateQueries`; the API stays the single source of truth.
4. **Feature folders.** Code is organized by domain feature, not by technical type.
5. **Small components.** Pages compose feature components; anything > ~150 lines is split.

## 2. Routes and pages

| Route | Page | Main content | Roles |
| --- | --- | --- | --- |
| `/login` | Login | Email/password form | anonymous |
| `/` | Dashboard | KPI cards (deployments 7d by status, pending approvals, open findings by severity, AI requests 24h), activity feed (live), charts | all |
| `/projects` | Projects list | Cards grouped by team; env badges with current version | all |
| `/projects/:slug` | Project overview | Environments strip (Dev/Staging/Prod with current artifact + "Deploy" action), recent deployments, latest artifact scan summary | all |
| `/projects/:slug/artifacts` | Artifacts | Table: version, branch, sha, checks (build/test/3 scanners as ✓/✗/–), deployed-to badges | all |
| `/projects/:slug/artifacts/:id` | Artifact detail | Metadata, scan summaries, findings tabs per scanner, "Request deployment" per environment | all |
| `/projects/:slug/environments/:envId` | Environment | Target config (Owner/Admin editable), runtime status, deployment history | all |
| `/projects/:slug/settings` | Project settings | Repository, API keys (create/revoke), archive | Owner/Admin |
| `/deployments` | Deployments | Filterable table (project, env, status, requester, date) | all |
| `/deployments/:id` | Deployment detail | Header (status, artifact, env), **live timeline**, **policy evaluation panel** (rule list ✓/✗ with messages), approvals panel (approve/reject with comment), AI summary button, cancel/rollback | all (actions by permission) |
| `/approvals` | Approval queue | Deployments awaiting the caller's approval | Approver/Security/Admin |
| `/security/findings` | Findings explorer | Cross-project findings, filters (severity/scanner/status/project), suppress with reason | all (suppress: Security/Admin) |
| `/security/findings/:id` | Finding detail | Description, location, package/fix, history across artifacts, AI explain | all |
| `/policies` | Policies | List by scope; enable/disable | all (edit: Security/Admin) |
| `/policies/new`, `/policies/:id` | Policy editor | Metadata + **rule builder** (add rule → type select → typed parameter form from `/policies/rule-types` schema), "Simulate" against an artifact/environment | Security/Admin |
| `/ai/chat` | AI playground | Model selector (allowed only), chat, governance panel showing detected/redacted types and rate-limit remaining | all |
| `/ai/requests` | AI request log | Table with status, model, detected types, latency; drill-in | own / Security / Admin |
| `/ai/policies` | AI policies | Global + team policies editor | Security/Admin |
| `/audit` | Audit log | Filterable table, detail drawer with metadata JSON, export | Security/Admin |
| `/admin/users` | Users | Create, roles, activate | Admin |
| `/admin/teams` | Teams | Create, members | Admin |
| `/admin/models` | AI models | Sync from Ollama, enable/disable | Admin |
| `*` | Not found | | |

Layout: persistent left sidebar (nav filtered by permissions) + top bar (search, connection
status dot for SignalR, user menu). Route guards: `RequireAuth`, `RequirePermission`.

## 3. Project structure

```
src/AegisOps.Web/
├── index.html
├── vite.config.ts
├── tailwind.config.ts (v4: mostly CSS-first config in index.css)
├── src/
│   ├── main.tsx                     # providers: QueryClient, Router, Auth, Realtime, Toaster
│   ├── app/
│   │   ├── router.tsx               # route tree, lazy pages
│   │   ├── layout/                  # AppShell, Sidebar, TopBar
│   │   └── guards/                  # RequireAuth, RequirePermission
│   ├── lib/
│   │   ├── api/
│   │   │   ├── client.ts            # Axios instance, interceptors (auth, refresh, problem details)
│   │   │   ├── problem.ts           # ProblemDetails type + toast mapping
│   │   │   └── generated/           # OpenAPI-generated types (openapi-typescript) — never hand-edited
│   │   ├── realtime/
│   │   │   ├── connection.ts        # SignalR HubConnection lifecycle, reconnect, token factory
│   │   │   └── useRealtimeInvalidation.ts  # message → queryKey invalidation map
│   │   ├── auth/                    # AuthProvider, useAuth, usePermission
│   │   ├── query/                   # queryClient, queryKeys factory
│   │   └── utils/                   # dates, formatting, cn()
│   ├── components/ui/               # Button, Card, Table, Dialog, Badge, Tabs, Toast… (Tailwind)
│   ├── features/
│   │   ├── dashboard/
│   │   ├── projects/                # api.ts, hooks.ts, components/, pages/
│   │   ├── artifacts/
│   │   ├── deployments/             # DeploymentTimeline, PolicyEvaluationPanel, ApprovalPanel
│   │   ├── approvals/
│   │   ├── findings/
│   │   ├── policies/                # RuleBuilder, rule parameter forms per type
│   │   ├── ai/                      # ChatPlayground, GovernancePanel, RequestsTable
│   │   ├── audit/
│   │   └── admin/
│   └── test/                        # Vitest setup, MSW handlers
```

Each feature folder follows the same shape: `api.ts` (typed calls), `hooks.ts`
(`useDeployments`, `useApproveDeployment`), `components/`, `pages/`.

## 4. Data layer

### Query keys

```ts
export const qk = {
  me: ['me'] as const,
  projects: { all: ['projects'] as const, detail: (slug: string) => ['projects', slug] as const },
  deployments: {
    list: (f: DeploymentFilters) => ['deployments', f] as const,
    detail: (id: string) => ['deployments', id] as const,
    events: (id: string) => ['deployments', id, 'events'] as const,
    evaluation: (id: string) => ['deployments', id, 'evaluation'] as const,
  },
  approvals: { pending: ['approvals', 'pending'] as const },
  // …
};
```

### Realtime → invalidation map

| Message | Invalidates |
| --- | --- |
| `DeploymentCreated` | `deployments.list(*)`, `projects.detail(slug)`, dashboard |
| `DeploymentStatusChanged` | `deployments.detail(id)`, `deployments.list(*)`, `approvals.pending`, env detail |
| `DeploymentEventAppended` | `deployments.events(id)` (or append via `setQueryData` for smooth timeline — the one allowed exception, guarded by `sequence` ordering) |
| `ApprovalRequested` | `approvals.pending` + toast for approvers |
| `ScanCompleted` | artifact detail, findings lists |

Defaults: `staleTime: 30s`, `refetchOnWindowFocus: true`, `retry: 1`. Lists that matter
live (deployments, approvals) also poll every 60 s as a fallback when SignalR is
disconnected (`refetchInterval` conditional on connection state).

### Mutations

Optimistic updates are avoided for governance actions (approve/reject/suppress) — the
server response is authoritative. Mutations invalidate relevant keys `onSuccess` and show
Problem Details `detail` on error.

## 5. Key components

| Component | Notes |
| --- | --- |
| `DeploymentTimeline` | Vertical list of `DeploymentEvent`s with icons per type, relative + absolute time, expandable `data`; auto-scrolls on append; "Live" indicator |
| `PolicyEvaluationPanel` | Grouped by policy; each rule row: effect badge (Deny/RequireApproval/Warn), ✓/✗, message, expandable evidence; header shows decision and approvals `n/m` |
| `ApprovalPanel` | Shows required roles, received approvals with comments, approve/reject form; hidden if caller cannot approve; explains why (e.g. "You requested this deployment") |
| `ScanSummaryBadges` | Compact `C H M L` counts per scanner with colours |
| `RuleBuilder` | Dynamic form driven by `/policies/rule-types` JSON schema → zod schema; per-type field components (`MaxFindingsFields`, `DeploymentWindowFields` with weekday/time pickers) |
| `GovernancePanel` (AI) | Shows effective policy name, allowed models, remaining rate limit, detected sensitive types from the last response |
| `SeverityBadge`, `StatusBadge` | Single source for colours; status colours: Requested/Evaluating gray, AwaitingApproval amber, Approved/Deploying blue, Succeeded green, Denied/Rejected/Failed red, Cancelled slate |

## 6. Forms & validation

react-hook-form + zod schemas colocated with each form. Server validation errors
(`errors` dictionary) are mapped onto fields via `setError`. Policy rule parameter schemas
are generated from the API's rule-type catalogue to avoid drift.

## 7. Type safety with the API

`openapi-typescript` generates `lib/api/generated/schema.d.ts` from `/openapi/v1.json` in a
`npm run gen:api` script (run in CI to fail on drift). Feature `api.ts` files import these
types; no hand-written DTO duplicates.

## 8. Accessibility & UX

- Keyboard navigable tables and dialogs; focus trapping in modals.
- Colour is never the only status signal (icons + text).
- Empty states with a primary action ("No projects yet → Create project").
- Dark mode via Tailwind `dark:` (system preference).

## 9. Testing

- **Vitest + Testing Library** for components (`PolicyEvaluationPanel` rendering of
  decisions, `RuleBuilder` validation).
- **MSW** for API mocking in component tests.
- **Playwright** (V2) for the four journeys against the Compose stack.

## 10. Build & serve

`vite build` → static assets → nginx image (`deploy/web/nginx.conf`) serving the SPA with
history fallback and proxying `/api`, `/hubs`, `/openapi`, `/scalar` to `api:8080`
(WebSocket upgrade enabled for `/hubs`). `VITE_*` variables are not needed since the UI uses
relative URLs.
