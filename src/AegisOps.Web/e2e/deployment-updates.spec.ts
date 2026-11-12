import { expect, test } from "@playwright/test";

const deploymentChanged =
  JSON.stringify({
    type: 1,
    target: "DeploymentChanged",
    arguments: ["00000000-0000-7000-8000-000000000001"],
  }) + "\u001e";

test("both signed-in browser sessions refresh after a deployment update", async ({ browser }) => {
  let deployments = [{ status: "Succeeded" }];
  const sendUpdate = new Array<(() => void) | undefined>(2);
  const contexts = await Promise.all([browser.newContext(), browser.newContext()]);

  try {
    const pages = await Promise.all(
      contexts.map(async (context, index) => {
        await context.route("**/api/v1/auth/login", (route) =>
          route.fulfill({
            json: { accessToken: `e2e-session-${index}`, expiresAt: "2030-01-01T00:00:00Z" },
          }),
        );
        await context.route("**/api/v1/deployments/approval-queue", (route) => route.fulfill({ json: [] }));
        await context.route("**/api/v1/deployments", (route) => route.fulfill({ json: deployments }));
        await context.route("**/hubs/ops/negotiate*", (route) =>
          route.fulfill({
            json: {
              connectionId: `e2e-connection-${index}`,
              connectionToken: `e2e-connection-token-${index}`,
              negotiateVersion: 1,
              availableTransports: [{ transport: "WebSockets", transferFormats: ["Text"] }],
            },
          }),
        );
        await context.routeWebSocket("**/hubs/ops*", (socket) => {
          socket.onMessage((message) => {
            if (String(message).includes('"protocol":"json"')) {
              socket.send("{}\u001e");
              sendUpdate[index] = () => socket.send(deploymentChanged);
            }
          });
        });

        const page = await context.newPage();
        await page.goto("/");
        await page.getByLabel("Password").fill("not-a-real-password");
        await page.getByRole("button", { name: "Continue" }).click();
        return page;
      }),
    );

    for (const page of pages) {
      await expect(page.getByRole("status").filter({ hasText: /^Live$/ })).toBeVisible();
      await expect(page.locator(".stat-card").filter({ hasText: "Succeeded" }).locator("strong")).toHaveText("1");
    }
    await expect.poll(() => sendUpdate.filter((send) => send !== undefined).length).toBe(2);

    deployments = [{ status: "Succeeded" }, { status: "Succeeded" }];
    for (const send of sendUpdate) send?.();

    for (const page of pages) {
      await expect(page.locator(".stat-card").filter({ hasText: "Succeeded" }).locator("strong")).toHaveText("2");
    }
  } finally {
    await Promise.all(contexts.map((context) => context.close()));
  }
});

test("approvers can generate a deployment summary and explain findings", async ({ page }) => {
  const deploymentId = "00000000-0000-7000-8000-000000000101";
  const findingId = "00000000-0000-7000-8000-000000000202";
  await page.route("**/api/v1/auth/login", (route) =>
    route.fulfill({
      json: { accessToken: "e2e-session", expiresAt: "2030-01-01T00:00:00Z" },
    }),
  );
  await page.route("**/api/v1/deployments/approval-queue", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/deployments", (route) =>
    route.fulfill({
      json: [{ id: deploymentId, status: "AwaitingApproval", approvalsReceived: 0, approvalsRequired: 1 }],
    }),
  );
  await page.route(`**/api/v1/deployments/${deploymentId}`, (route) =>
    route.fulfill({
      json: { artifactId: "00000000-0000-7000-8000-000000000303", status: "AwaitingApproval" },
    }),
  );
  await page.route(`**/api/v1/deployments/${deploymentId}/events`, (route) => route.fulfill({ json: [] }));
  await page.route(`**/api/v1/deployments/${deploymentId}/evaluation`, (route) =>
    route.fulfill({
      json: { decision: "RequireApproval", ruleResults: [] },
    }),
  );
  await page.route("**/api/v1/artifacts/*/findings", (route) =>
    route.fulfill({
      json: [
        { id: findingId, severity: "High", title: "Outdated vulnerable package", cve: "CVE-2026-1234", status: "Open" },
      ],
    }),
  );
  await page.route(`**/api/v1/deployments/${deploymentId}/ai-summary`, (route) =>
    route.fulfill({
      json: {
        summary: "Review the high-severity dependency before approval.",
        label: "AI-generated — verify before acting.",
      },
    }),
  );
  await page.route(`**/api/v1/findings/${findingId}/ai-explain`, (route) =>
    route.fulfill({
      json: { explanation: "Upgrade the package to the fixed release.", label: "AI-generated — verify before acting." },
    }),
  );
  await page.route("**/hubs/ops/negotiate*", (route) =>
    route.fulfill({
      json: {
        connectionId: "e2e-connection",
        connectionToken: "e2e-connection-token",
        negotiateVersion: 1,
        availableTransports: [{ transport: "WebSockets", transferFormats: ["Text"] }],
      },
    }),
  );
  await page.routeWebSocket("**/hubs/ops*", (socket) => {
    socket.onMessage((message) => {
      if (String(message).includes('"protocol":"json"')) socket.send("{}\u001e");
    });
  });

  await page.goto("/");
  await page.getByLabel("Password").fill("not-a-real-password");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByRole("button", { name: "Deployments", exact: true }).click();
  await page.getByRole("button", { name: /Awaiting approval/ }).click();

  await page.getByRole("button", { name: "Generate AI summary" }).click();
  await expect(page.getByText("Review the high-severity dependency before approval.")).toBeVisible();
  await page.getByRole("button", { name: "Explain with AI" }).click();
  await expect(page.getByText("Upgrade the package to the fixed release.")).toBeVisible();
});

test("AI governance shows request history and saves policy controls", async ({ page }) => {
  let savedPolicy: Record<string, unknown> | undefined;
  const policy = {
    id: "00000000-0000-7000-8000-000000000404",
    name: "Global baseline",
    scope: "Global",
    scopeId: null,
    allowedModelIdsJson: '["00000000-0000-7000-8000-000000000505"]',
    requestsPerMinute: 20,
    requestsPerDay: 500,
    maxPromptChars: 16000,
    sensitiveDataAction: "Redact",
    blockedPatternsJson: "[]",
    redactPii: true,
    storeContent: false,
    isEnabled: true,
    version: 2,
    updatedAt: "2026-11-03T00:00:00Z",
  };
  await page.route("**/api/v1/auth/login", (route) =>
    route.fulfill({
      json: { accessToken: "e2e-session", expiresAt: "2030-01-01T00:00:00Z" },
    }),
  );
  await page.route("**/api/v1/deployments/approval-queue", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/deployments", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/ai/requests*", (route) =>
    route.fulfill({
      json: {
        page: 1,
        pageSize: 20,
        total: 1,
        items: [
          {
            id: "00000000-0000-7000-8000-000000000606",
            purpose: "Chat",
            status: "Blocked",
            promptChars: 120,
            responseChars: 0,
            promptTokens: null,
            completionTokens: null,
            latencyMs: 0,
            detectedSensitiveTypesJson: '["AWS_ACCESS_KEY"]',
            redactionsApplied: 0,
            blockReason: "SensitiveData",
            createdAt: "2026-11-03T01:00:00Z",
          },
        ],
      },
    }),
  );
  await page.route("**/api/v1/ai/policies", async (route) => {
    if (route.request().method() === "PUT") {
      savedPolicy = route.request().postDataJSON() as Record<string, unknown>;
      await route.fulfill({
        json: { id: policy.id, name: policy.name, scope: policy.scope, scopeId: null, version: 3 },
      });
      return;
    }
    await route.fulfill({ json: [policy] });
  });
  await page.route("**/hubs/ops/negotiate*", (route) =>
    route.fulfill({
      json: {
        connectionId: "e2e-connection",
        connectionToken: "e2e-connection-token",
        negotiateVersion: 1,
        availableTransports: [{ transport: "WebSockets", transferFormats: ["Text"] }],
      },
    }),
  );
  await page.routeWebSocket("**/hubs/ops*", (socket) => {
    socket.onMessage((message) => {
      if (String(message).includes('"protocol":"json"')) socket.send("{}\u001e");
    });
  });

  await page.goto("/");
  await page.getByLabel("Password").fill("not-a-real-password");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByRole("button", { name: "AI Governance", exact: true }).click();
  await expect(page.getByRole("cell", { name: "SensitiveData", exact: true })).toBeVisible();
  await page.getByLabel("Requests per minute").fill("25");
  await page.getByRole("button", { name: "Save AI policy" }).click();
  await expect.poll(() => savedPolicy?.requestsPerMinute).toBe(25);
});

test("production policy can add and configure the weekend deployment window", async ({ page }) => {
  let policy: { version: number } & Record<string, unknown> = {
    id: "00000000-0000-7000-8000-000000000707",
    name: "Production baseline",
    description: "Tests pass and production requires two approvals.",
    scope: "Global",
    scopeId: null,
    isEnabled: true,
    appliesToTiers: ["Production"],
    version: 2,
    rules: [
      { id: "rule-tests", type: "RequireTestsPassed", effect: "Deny", order: 0, isEnabled: true, parameters: {} },
      {
        id: "rule-approvals",
        type: "RequireApprovals",
        effect: "RequireApproval",
        order: 1,
        isEnabled: true,
        parameters: { count: 2 },
      },
    ],
  };
  const writes: Array<Record<string, unknown>> = [];
  const template = {
    type: "DeploymentWindow",
    effect: "Deny",
    parameters: {
      timezone: "Asia/Kuala_Lumpur",
      allowed: [{ days: ["Mon", "Tue", "Wed", "Thu"], from: "09:00", to: "18:00" }],
    },
  };

  await page.route("**/api/v1/auth/login", (route) =>
    route.fulfill({
      json: { accessToken: "e2e-session", expiresAt: "2030-01-01T00:00:00Z" },
    }),
  );
  await page.route("**/api/v1/deployments/approval-queue", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/deployments", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/policies**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith("/rule-types")) {
      await route.fulfill({ json: [template] });
    } else if (route.request().method() === "PUT") {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      writes.push(body);
      policy = { ...policy, ...body, version: policy.version + 1 };
      await route.fulfill({ json: policy });
    } else {
      await route.fulfill({ json: [policy] });
    }
  });
  await page.route("**/hubs/ops/negotiate*", (route) =>
    route.fulfill({
      json: {
        connectionId: "e2e-connection",
        connectionToken: "e2e-connection-token",
        negotiateVersion: 1,
        availableTransports: [{ transport: "WebSockets", transferFormats: ["Text"] }],
      },
    }),
  );
  await page.routeWebSocket("**/hubs/ops*", (socket) => {
    socket.onMessage((message) => {
      if (String(message).includes('"protocol":"json"')) socket.send("{}\u001e");
    });
  });

  await page.goto("/");
  await page.getByLabel("Password").fill("not-a-real-password");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByRole("button", { name: "Policies", exact: true }).click();
  await page.getByLabel("Rule to add").selectOption("DeploymentWindow");
  await page.getByRole("button", { name: "Add rule" }).click();

  const windowParameters = {
    timezone: "Asia/Kuala_Lumpur",
    allowed: [
      { days: ["Mon"], from: "08:00", to: "23:59:59.9999999" },
      { days: ["Tue", "Wed", "Thu"], from: "00:00", to: "23:59:59.9999999" },
      { days: ["Fri"], from: "00:00", to: "17:59:59.9999999" },
    ],
  };
  await page
    .getByLabel("Production baseline Deployment window parameters")
    .fill(JSON.stringify(windowParameters, null, 2));
  await page.getByRole("button", { name: "Save Deployment window parameters" }).click();

  await expect.poll(() => writes.length).toBe(2);
  expect(writes[0]).toMatchObject({
    name: "Production baseline",
    description: "Tests pass and production requires two approvals.",
    scope: "Global",
    scopeId: null,
  });
  expect(writes[1]).toMatchObject({
    name: "Production baseline",
    description: "Tests pass and production requires two approvals.",
    scope: "Global",
    scopeId: null,
    rules: expect.arrayContaining([
      expect.objectContaining({ type: "DeploymentWindow", effect: "Deny", parameters: windowParameters }),
    ]),
  });
  await expect(page.getByRole("heading", { name: /Production baseline v4/ })).toBeVisible();
});
