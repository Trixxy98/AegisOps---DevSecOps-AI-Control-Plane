import type { Page, Route } from "@playwright/test";

// Realistic mocked data for every screen, shaped like the API handlers in src/AegisOps.Api (camelCase JSON):
// DeploymentEndpoints.cs, ListProjectFindings.cs, PolicyPhase1Endpoints.cs, AiEndpoints.cs, ListUsers.cs, AdminJobs.cs.
// No real backend is involved; unknown paths fall through to the routes registered by mockSession.ts.

/** A UUIDv7-looking identifier that is stable per seed. */
function guid(seed: number): string {
  const hex = (value: number, length: number) => value.toString(16).padStart(length, "0").slice(-length);
  return `019a7${hex(seed * 7919, 3)}-${hex(0x5b21 + seed * 31, 4)}-7${hex(seed * 977, 3)}-9${hex(seed * 131, 3)}-${hex(seed * 2654435761, 12)}`;
}

const at = (day: number, time: string) => `2026-11-${String(day).padStart(2, "0")}T${time}:00Z`;

export const currentUser = {
  id: guid(1),
  email: "aisyah@aegisops.local",
  displayName: "Aisyah Rahman",
  roles: ["Admin", "Security"],
};

export const permissions = [
  "findings:read",
  "findings:suppress",
  "ai:chat",
  "projects:write",
  "environments:write",
  "users:manage",
  "roles:manage",
];

const teams = [
  { id: guid(10), name: "Platform", slug: "platform", description: "Shared services", myRole: null },
  { id: guid(11), name: "Payments", slug: "payments", description: null, myRole: null },
];

const projects = [
  { slug: "circuit-service", name: "Circuit service" },
  { slug: "notification-service", name: "Notification service" },
  { slug: "site-service", name: "Site service" },
  { slug: "service-template", name: "Service template" },
];

const environments = [
  {
    id: guid(20),
    name: "Development",
    tier: "Development",
    targetType: "noop",
    containerName: null,
    network: null,
    hostPort: 0,
    containerPort: 0,
    healthPath: "/health",
    healthTimeoutSeconds: 0,
  },
  {
    id: guid(21),
    name: "Staging",
    tier: "Staging",
    targetType: "docker",
    containerName: "circuit-staging",
    network: "aegisops-staging",
    hostPort: 8102,
    containerPort: 8080,
    healthPath: "/health",
    healthTimeoutSeconds: 60,
  },
  {
    id: guid(22),
    name: "Production",
    tier: "Production",
    targetType: "docker",
    containerName: "circuit-prod",
    network: "aegisops-prod",
    hostPort: 8202,
    containerPort: 8080,
    healthPath: "/health/ready",
    healthTimeoutSeconds: 90,
  },
];

const artifacts = [
  { id: guid(30), version: "v1.5.0-rc.1", branch: "release/1.5" },
  { id: guid(31), version: "v1.4.2", branch: "main" },
  { id: guid(32), version: "v1.4.1", branch: "main" },
  { id: guid(33), version: "v0.0.0-ci.replay", branch: "main" },
];

const apiKeys = [
  {
    id: guid(40),
    name: "github-actions",
    keyPrefix: "aeg_7Kq2",
    scopes: ["artifacts:write", "scans:write", "deployments:request"],
    expiresAt: at(30, "00:00"),
    lastUsedAt: at(12, "08:41"),
    revokedAt: null,
    createdAt: at(2, "10:00"),
  },
  {
    id: guid(41),
    name: "local-laptop",
    keyPrefix: "aeg_Pz9x",
    scopes: ["artifacts:write"],
    expiresAt: at(30, "00:00"),
    lastUsedAt: null,
    revokedAt: at(9, "16:20"),
    createdAt: at(3, "09:12"),
  },
];

const variables: Record<string, Array<{ name: string; isSecret: boolean; value: string | null; updatedAt: string }>> = {
  [guid(21)]: [
    {
      name: "ConnectionStrings__Default",
      isSecret: false,
      value: "Host=db;Database=circuit",
      updatedAt: at(11, "09:00"),
    },
    { name: "DB_PASSWORD", isSecret: true, value: null, updatedAt: at(11, "09:00") },
  ],
  [guid(22)]: [{ name: "DB_PASSWORD", isSecret: true, value: null, updatedAt: at(10, "14:30") }],
};

const deployments = [
  {
    id: guid(50),
    status: "AwaitingApproval",
    approvalsRequired: 2,
    approvalsReceived: 1,
    requestedAt: at(12, "09:14"),
  },
  { id: guid(51), status: "Deploying", approvalsRequired: 0, approvalsReceived: 0, requestedAt: at(12, "08:52") },
  { id: guid(52), status: "Succeeded", approvalsRequired: 0, approvalsReceived: 0, requestedAt: at(11, "17:05") },
  {
    id: guid(53),
    status: "Denied",
    approvalsRequired: 0,
    approvalsReceived: 0,
    requestedAt: at(11, "15:40"),
    failureReason: "Staging gate: Trivy scan is missing or older than 7 days.",
  },
  {
    id: guid(54),
    status: "Failed",
    approvalsRequired: 1,
    approvalsReceived: 1,
    requestedAt: at(10, "11:22"),
    failureReason: "Health check GET /health/ready returned 503 after 90 s.",
  },
  { id: guid(55), status: "Succeeded", approvalsRequired: 2, approvalsReceived: 2, requestedAt: at(9, "10:03") },
  { id: guid(56), status: "Rejected", approvalsRequired: 2, approvalsReceived: 0, requestedAt: at(8, "13:47") },
].map((item) => ({ projectId: guid(60), environmentId: guid(22), artifactId: guid(30), failureReason: null, ...item }));

const approvalQueue = [
  { id: guid(50), projectId: guid(60), requestedAt: at(12, "09:14"), status: "AwaitingApproval" },
  { id: guid(57), projectId: guid(61), requestedAt: at(12, "07:30"), status: "AwaitingApproval" },
];

const evaluation = {
  decision: "RequireApproval",
  ruleResults: [
    { type: "RequireTestsPassed", passed: true, message: "Tests passed in CI run 4812." },
    { type: "RequireFreshScans", passed: true, message: "Gitleaks, Trivy and Semgrep ran within 7 days." },
    { type: "MaxSeverity", passed: true, message: "No open Critical findings." },
    { type: "RequireApprovals", passed: false, message: "1 of 2 approvals received." },
  ],
};

const deploymentEvents = [
  { sequence: 1, type: "Requested", message: "Requested by dev@aegisops.local for Production." },
  { sequence: 2, type: "PolicyEvaluated", message: "Decision RequireApproval (Production baseline v4)." },
  { sequence: 3, type: "ApprovalRequested", message: "Two approvals required from people other than the requester." },
  { sequence: 4, type: "ApprovalReceived", message: "Approved by security@aegisops.local." },
];

const findingBase = {
  scanId: guid(70),
  description: null,
  filePath: null,
  startLine: null,
  packageName: null,
  installedVersion: null,
  fixedVersion: null,
  cve: null,
  helpUri: null,
  suppressionReason: null,
  suppression: null,
  artifactId: guid(30),
  artifactVersion: "v1.5.0-rc.1",
};

const findings = [
  {
    ...findingBase,
    id: guid(80),
    scanner: "Trivy",
    severity: "Critical",
    ruleId: "CVE-2024-6387",
    title: "OpenSSH regreSSHion remote code execution",
    description: "A signal handler race condition in sshd allows unauthenticated remote code execution as root.",
    filePath: "Dockerfile",
    startLine: 4,
    packageName: "openssh-server",
    installedVersion: "1:9.2p1-2",
    fixedVersion: "1:9.2p1-2+deb12u3",
    cve: "CVE-2024-6387",
    helpUri: "https://avd.aquasec.com/nvd/cve-2024-6387",
    fingerprint: "trivy:openssh-server:CVE-2024-6387",
    status: "Open",
  },
  {
    ...findingBase,
    id: guid(81),
    scanner: "Semgrep",
    severity: "High",
    ruleId: "csharp.lang.security.sqli.raw-sql",
    title: "SQL built from user input",
    description: "String concatenation passes request data into FromSqlRaw.",
    filePath: "src/Circuit.Api/Repositories/CircuitRepository.cs",
    startLine: 88,
    fingerprint: "semgrep:raw-sql:CircuitRepository.cs:88",
    status: "Open",
  },
  {
    ...findingBase,
    id: guid(82),
    scanner: "Trivy",
    severity: "High",
    ruleId: "CVE-2025-21172",
    title: ".NET remote code execution in MSDIA",
    packageName: "Microsoft.NETCore.App",
    installedVersion: "9.0.0",
    fixedVersion: "9.0.1",
    cve: "CVE-2025-21172",
    fingerprint: "trivy:dotnet:CVE-2025-21172",
    status: "Open",
  },
  {
    ...findingBase,
    id: guid(83),
    scanner: "Gitleaks",
    severity: "Medium",
    ruleId: "generic-api-key",
    title: "Generic API key in test fixture",
    filePath: "tests/fixtures/appsettings.Test.json",
    startLine: 12,
    fingerprint: "gitleaks:generic-api-key:appsettings.Test.json:12",
    status: "Suppressed",
    suppressionReason: "Test fixture key, not a real credential.",
    suppression: { reason: "Test fixture key, not a real credential.", createdAt: at(6, "12:00"), expiresAt: null },
  },
  {
    ...findingBase,
    id: guid(84),
    scanner: "Semgrep",
    severity: "Medium",
    ruleId: "csharp.dotnet.security.missing-antiforgery",
    title: "POST endpoint without antiforgery validation",
    filePath: "src/Circuit.Api/Endpoints/Circuits.cs",
    startLine: 41,
    fingerprint: "semgrep:antiforgery:Circuits.cs:41",
    status: "Open",
  },
  {
    ...findingBase,
    id: guid(85),
    scanner: "Trivy",
    severity: "Low",
    ruleId: "CVE-2023-4039",
    title: "GCC -fstack-protector bypass on arm64",
    packageName: "libgcc-s1",
    installedVersion: "12.2.0-14",
    fixedVersion: null,
    cve: "CVE-2023-4039",
    fingerprint: "trivy:libgcc:CVE-2023-4039",
    status: "Open",
  },
  {
    ...findingBase,
    id: guid(86),
    scanner: "Semgrep",
    severity: "Info",
    ruleId: "generic.ci.security.pinned-actions",
    title: "Workflow action not pinned to a commit SHA",
    filePath: ".github/workflows/ci.yml",
    startLine: 23,
    fingerprint: "semgrep:pinned-actions:ci.yml:23",
    status: "Resolved",
  },
];

const policies = [
  {
    id: guid(90),
    name: "Production baseline",
    description: "Tests pass, no open Critical findings and two approvals.",
    scope: "Global",
    scopeId: null,
    isEnabled: true,
    appliesToTiers: ["Production"],
    version: 4,
    rules: [
      { id: guid(91), type: "RequireTestsPassed", effect: "Deny", order: 0, isEnabled: true, parameters: {} },
      {
        id: guid(92),
        type: "MaxSeverity",
        effect: "Deny",
        order: 1,
        isEnabled: true,
        parameters: { maxSeverity: "High" },
      },
      {
        id: guid(93),
        type: "RequireApprovals",
        effect: "RequireApproval",
        order: 2,
        isEnabled: true,
        parameters: { count: 2 },
      },
    ],
  },
  {
    id: guid(94),
    name: "Staging gate",
    description: "Fresh Gitleaks, Trivy and Semgrep scans before Staging.",
    scope: "Global",
    scopeId: null,
    isEnabled: true,
    appliesToTiers: ["Staging"],
    version: 2,
    rules: [
      {
        id: guid(95),
        type: "RequireFreshScans",
        effect: "Deny",
        order: 0,
        isEnabled: true,
        parameters: { scanners: ["Gitleaks", "Trivy", "Semgrep"], maxAgeDays: 7 },
      },
    ],
  },
];

const ruleTypes = [
  { type: "DeploymentWindow", effect: "Deny", parameters: { timezone: "Asia/Kuala_Lumpur", allowed: [] } },
  { type: "MaxSeverity", effect: "Deny", parameters: { maxSeverity: "High" } },
  { type: "RequireApprovals", effect: "RequireApproval", parameters: { count: 1 } },
  { type: "RequireFreshScans", effect: "Deny", parameters: { maxAgeDays: 7 } },
  { type: "RequireTestsPassed", effect: "Deny", parameters: {} },
];

const aiRequests = [
  ["Chat", "Completed", 412, 1380, 96, 311, 2140, "[]", 0, null, at(12, "09:02")],
  ["DeploymentSummary", "Completed", 2960, 842, 712, 190, 3870, "[]", 0, null, at(12, "08:47")],
  ["Chat", "Completed", 188, 604, 41, 140, 980, '["EMAIL"]', 1, null, at(11, "16:31")],
  ["Chat", "Blocked", 120, 0, null, null, 0, '["AWS_ACCESS_KEY"]', 0, "SensitiveData", at(11, "15:12")],
  ["FindingExplanation", "Completed", 1544, 1102, 380, 254, 2960, "[]", 0, null, at(11, "11:05")],
  ["Chat", "Blocked", 64, 0, null, null, 0, "[]", 0, "RateLimited", at(10, "18:44")],
  ["Chat", "Failed", 230, 0, null, null, 30000, "[]", 0, "OllamaUnavailable", at(10, "09:20")],
].map((row, index) => ({
  id: guid(100 + index),
  purpose: row[0],
  status: row[1],
  promptChars: row[2],
  responseChars: row[3],
  promptTokens: row[4],
  completionTokens: row[5],
  latencyMs: row[6],
  detectedSensitiveTypesJson: row[7],
  redactionsApplied: row[8],
  blockReason: row[9],
  createdAt: row[10],
}));

const aiPolicies = [
  {
    id: guid(110),
    name: "Global baseline",
    scope: "Global",
    scopeId: null,
    allowedModelIdsJson: JSON.stringify([guid(120), guid(121)]),
    requestsPerMinute: 20,
    requestsPerDay: 500,
    maxPromptChars: 16000,
    sensitiveDataAction: "Redact",
    blockedPatternsJson: JSON.stringify(["(?i)BEGIN RSA PRIVATE KEY"]),
    redactPii: true,
    storeContent: false,
    isEnabled: true,
    version: 3,
    updatedAt: at(9, "12:00"),
  },
  {
    id: guid(111),
    name: "Platform team",
    scope: "Team",
    scopeId: guid(10),
    allowedModelIdsJson: JSON.stringify([guid(120)]),
    requestsPerMinute: 10,
    requestsPerDay: 200,
    maxPromptChars: 8000,
    sensitiveDataAction: "Block",
    blockedPatternsJson: "[]",
    redactPii: true,
    storeContent: false,
    isEnabled: true,
    version: 1,
    updatedAt: at(4, "12:00"),
  },
];

const aiModels = [
  { id: guid(120), name: "llama3.2:3b", displayName: "Llama 3.2 3B", provider: "Ollama" },
  { id: guid(121), name: "qwen2.5-coder:7b", displayName: null, provider: "Ollama" },
];

const auditEvents = [
  ["deployment.requested", "Succeeded", "dev@aegisops.local", at(12, "09:14")],
  ["deployment.evaluated", "Succeeded", "worker", at(12, "09:14")],
  ["approval.recorded", "Succeeded", "security@aegisops.local", at(12, "09:20")],
  ["ai.request", "Succeeded", "aisyah@aegisops.local", at(12, "09:02")],
  ["deployment.succeeded", "Succeeded", "worker", at(11, "17:07")],
  ["finding.suppressed", "Succeeded", "security@aegisops.local", at(11, "16:02")],
  ["authorization.denied", "Denied", "dev@aegisops.local", at(11, "15:50")],
  ["deployment.failed", "Failed", "worker", at(10, "11:24")],
  ["policy.updated", "Succeeded", "aisyah@aegisops.local", at(10, "10:15")],
  ["apikey.revoked", "Succeeded", "aisyah@aegisops.local", at(9, "16:20")],
  ["auth.login_failed", "Denied", "unknown@example.com", at(9, "07:58")],
  ["user.activated", "Succeeded", "aisyah@aegisops.local", at(8, "12:30")],
].map((row, index) => ({
  id: guid(130 + index),
  action: row[0],
  outcome: row[1],
  actorDisplay: row[2],
  actorType: row[2] === "worker" ? "System" : "User",
  resourceType: "Deployment",
  resourceId: guid(50),
  timestamp: row[3],
}));

const users = [
  { ...currentUser, isActive: true },
  { id: guid(2), email: "dev@aegisops.local", displayName: "Daniel Lim", isActive: true, roles: ["Developer"] },
  {
    id: guid(3),
    email: "security@aegisops.local",
    displayName: "Priya Nair",
    isActive: true,
    roles: ["Security", "Approver"],
  },
  { id: guid(4), email: "approver@aegisops.local", displayName: "Hafiz Osman", isActive: true, roles: ["Approver"] },
  { id: guid(5), email: "contractor@example.com", displayName: "Sam Taylor", isActive: false, roles: ["Developer"] },
];

const webhookDeliveries = [
  ["Succeeded", "Succeeded", 1, 5, null, at(12, "09:15")],
  ["ApprovalRequested", "Succeeded", 1, 5, null, at(12, "09:14")],
  ["Failed", "DeadLettered", 5, 5, "POST returned 502 Bad Gateway.", at(10, "11:25")],
  ["DeployStarted", "Succeeded", 2, 5, null, at(10, "11:22")],
  ["Requested", "Pending", 0, 5, null, at(12, "09:14")],
].map((row, index) => ({
  eventId: guid(140 + index),
  eventType: row[0],
  status: row[1],
  attempts: row[2],
  maxAttempts: row[3],
  lastError: row[4],
  occurredAt: row[5],
  completedAt: row[1] === "Succeeded" ? row[5] : null,
}));

const deadJobs = [
  {
    id: guid(150),
    type: "SendNotification",
    status: "DeadLettered",
    attempts: 5,
    maxAttempts: 5,
    lastError: "Webhook endpoint returned 502 Bad Gateway.",
  },
  {
    id: guid(151),
    type: "ParseScanReport",
    status: "DeadLettered",
    attempts: 3,
    maxAttempts: 3,
    lastError: "SARIF file exceeds the 10 MB limit.",
  },
];

function findingsPage(url: URL) {
  const params = url.searchParams;
  const matching = findings.filter(
    (item) =>
      (!params.get("severity") || item.severity === params.get("severity")) &&
      (!params.get("scanner") || item.scanner === params.get("scanner")) &&
      (!params.get("status") || item.status === params.get("status")),
  );
  const page = Number(params.get("page") ?? "1");
  const size = Number(params.get("pageSize") ?? "25");
  return { page, pageSize: size, total: matching.length, items: matching.slice((page - 1) * size, page * size) };
}

function respond(route: Route, url: URL): unknown {
  const path = url.pathname;
  const method = route.request().method();
  if (method === "GET") {
    if (path === "/api/v1/projects") return projects;
    if (path === "/api/v1/teams") return teams;
    if (path === "/api/v1/deployments") return deployments;
    if (path === "/api/v1/deployments/approval-queue") return approvalQueue;
    if (path === "/api/v1/policies") return policies;
    if (path === "/api/v1/policies/rule-types") return ruleTypes;
    if (path === "/api/v1/ai/requests") return { page: 1, pageSize: 20, total: aiRequests.length, items: aiRequests };
    if (path === "/api/v1/ai/policies") return aiPolicies;
    if (path === "/api/v1/ai/models") return aiModels;
    if (path === "/api/v1/audit") return auditEvents;
    if (path === "/api/v1/admin/users") return users;
    if (path === "/api/v1/admin/jobs/summary") return { pending: 2, running: 1, deadLettered: deadJobs.length };
    if (path === "/api/v1/admin/jobs") return deadJobs;
    if (/^\/api\/v1\/teams\/[^/]+\/webhook$/.test(path))
      return { url: "https://hooks.example.com/aegisops", secretConfigured: true, eventTypes: ["Succeeded", "Failed"] };
    if (/^\/api\/v1\/teams\/[^/]+\/webhook\/deliveries$/.test(path)) return webhookDeliveries;
    const project = /^\/api\/v1\/projects\/([^/]+)(\/.*)?$/.exec(path);
    if (project) {
      const rest = project[2] ?? "";
      if (rest === "") return { slug: project[1], environments };
      if (rest === "/artifacts") return artifacts;
      if (rest === "/api-keys") return apiKeys;
      if (rest === "/findings") return findingsPage(url);
      const variablesMatch = /^\/environments\/([^/]+)\/variables$/.exec(rest);
      if (variablesMatch) return variables[variablesMatch[1]] ?? [];
    }
    const deployment = /^\/api\/v1\/deployments\/([^/]+)(\/.*)?$/.exec(path);
    if (deployment) {
      const item = deployments.find((entry) => entry.id === deployment[1]) ?? deployments[0];
      if (!deployment[2]) return { ...item, previousArtifactId: guid(31) };
      if (deployment[2] === "/events") return deploymentEvents;
      if (deployment[2] === "/evaluation") return evaluation;
    }
    if (/^\/api\/v1\/artifacts\/[^/]+\/findings$/.test(path)) return findings.slice(0, 3);
  }
  if (method === "POST" && path === "/api/v1/ai/chat") {
    return {
      id: guid(160),
      model: "llama3.2:3b",
      message: {
        role: "assistant",
        content:
          "The deployment is waiting for a second approval. Policy checks passed: tests, fresh scans and no open Critical findings. Review the High-severity SQL finding in CircuitRepository.cs before approving.",
      },
      usage: { promptTokens: 58, completionTokens: 61, latencyMs: 1840 },
      governance: {
        policy: guid(110),
        sensitiveData: { action: "Redact", detected: ["EMAIL"], redactions: 1 },
        rateLimit: { remainingMinute: 19, remainingDay: 482 },
      },
    };
  }
  return undefined;
}

/** Registers one handler for the whole API; anything it does not know falls back to mockSession.ts routes. */
export async function mockWorkspace(page: Page) {
  await page.route("**/api/v1/**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname.startsWith("/api/v1/auth/") || url.pathname === "/api/v1/system/dependencies") {
      return route.fallback();
    }
    const body = respond(route, url);
    if (body === undefined) return route.fallback();
    return route.fulfill({ json: body });
  });
}
