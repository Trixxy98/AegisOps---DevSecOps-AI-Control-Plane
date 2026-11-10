import { expect, test, type Page, type WebSocketRoute } from "@playwright/test";

// P5-07/P5-08 with a mocked SignalR hub and mocked REST API (no real API, Redis, Worker or Docker).
// page.clock drives the 10-second fallback timers so "no polling while live" is checked over 25 s of app time.

const deploymentId = "00000000-0000-7000-8000-000000000901";
const record = "\u001e";
const deploymentChanged = JSON.stringify({ type: 1, target: "DeploymentChanged", arguments: [deploymentId] }) + record;

type TimelineEvent = { sequence: number; type: string; message: string };

async function mockApp(page: Page) {
  const hits = { list: 0, approvals: 0, detail: 0, events: 0 };
  const state = {
    hubDown: false,
    status: "Deploying",
    events: [{ sequence: 1, type: "DeployStarted", message: "Docker executor started for web." }] as TimelineEvent[],
    socket: undefined as WebSocketRoute | undefined,
    handshakes: 0,
    hitsAtHandshake: { ...hits },
  };

  await page.route("**/api/v1/auth/login", (route) =>
    route.fulfill({ json: { accessToken: "e2e-session", expiresAt: "2030-01-01T00:00:00Z" } }),
  );
  await page.route("**/api/v1/auth/me", (route) =>
    route.fulfill({
      json: { id: "00000000-0000-7000-8000-000000000001", email: "e2e@aegisops.local", permissions: [] },
    }),
  );
  await page.route("**/api/v1/system/dependencies", (route) =>
    route.fulfill({ json: { ollama: "Healthy", docker: "Healthy", worker: "Healthy" } }),
  );
  await page.route("**/api/v1/deployments/approval-queue", (route) => {
    hits.approvals++;
    return route.fulfill({ json: [] });
  });
  await page.route("**/api/v1/deployments", (route) => {
    hits.list++;
    return route.fulfill({
      json: [{ id: deploymentId, status: state.status, approvalsReceived: 0, approvalsRequired: 0 }],
    });
  });
  await page.route(`**/api/v1/deployments/${deploymentId}`, (route) => {
    hits.detail++;
    return route.fulfill({ json: { artifactId: "00000000-0000-7000-8000-000000000902", status: state.status } });
  });
  await page.route(`**/api/v1/deployments/${deploymentId}/events`, (route) => {
    hits.events++;
    return route.fulfill({ json: state.events });
  });
  await page.route(`**/api/v1/deployments/${deploymentId}/evaluation`, (route) =>
    route.fulfill({ json: { decision: "Allow", ruleResults: [] } }),
  );
  await page.route("**/api/v1/artifacts/*/findings", (route) => route.fulfill({ json: [] }));
  await page.route("**/hubs/ops/negotiate*", (route) =>
    state.hubDown
      ? route.fulfill({ status: 503, body: "hub unavailable" })
      : route.fulfill({
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
      const text = String(message);
      if (text.includes('"protocol":"json"')) {
        state.handshakes++;
        state.hitsAtHandshake = { ...hits };
        socket.send("{}" + record);
        state.socket = socket;
      } else if (text.includes('"type":6')) {
        // Answer keep-alive pings so the client's 30 s server timeout never fires under the fake clock.
        socket.send(JSON.stringify({ type: 6 }) + record);
      }
    });
  });
  return { hits, state };
}

async function signIn(page: Page) {
  await page.goto("/");
  await page.getByLabel("Password").fill("not-a-real-password");
  await page.getByRole("button", { name: "Continue" }).click();
}

const liveBadge = (page: Page) => page.getByRole("status").filter({ hasText: "Live updates" });
const pollingBadge = (page: Page) => page.getByRole("status").filter({ hasText: "Polling (10s)" });
const timeline = (page: Page) => page.locator("ol.item-list > li.detail-row");

test("live hub replaces polling; disconnect falls back to polling and reconnect catches up once", async ({ page }) => {
  await page.clock.install();
  const { hits, state } = await mockApp(page);
  await signIn(page);
  await expect(liveBadge(page)).toBeVisible();
  await page.getByRole("button", { name: "Deployments", exact: true }).click();
  await page.getByRole("button", { name: /Deploying/ }).click();
  await expect(timeline(page)).toHaveText(["DeployStarted: Docker executor started for web."]);

  // Connected: 25 s of app time (two and a half polling periods) sends no REST reads at all.
  let before = { ...hits };
  await page.clock.runFor(25_000);
  await page.waitForTimeout(250); // let any request a timer started reach the route handlers
  expect(hits).toEqual(before);

  // A hub push refreshes the view with exactly one reload of the list and the open timeline.
  state.events = [...state.events, { sequence: 2, type: "ImagePulled", message: "example/app:1.0.0" }];
  state.socket?.send(deploymentChanged);
  await expect(timeline(page)).toHaveText([
    "DeployStarted: Docker executor started for web.",
    "ImagePulled: example/app:1.0.0",
  ]);
  await expect.poll(() => hits.events).toBe(before.events + 1);

  // Disconnect and keep the hub unavailable: the client reconnects in the background and falls back to polling.
  state.hubDown = true;
  await state.socket?.close({ code: 1011, reason: "hub restarted" });
  await expect(pollingBadge(page)).toBeVisible();
  state.events = [...state.events, { sequence: 3, type: "ContainerStarted", message: "web" }];
  before = { ...hits };
  await page.clock.runFor(10_500);
  await expect.poll(() => hits.list).toBeGreaterThan(before.list);
  await expect.poll(() => hits.approvals).toBeGreaterThan(before.approvals);
  await expect.poll(() => hits.events).toBeGreaterThan(before.events);
  await expect(timeline(page)).toHaveCount(3); // the polled timeline shows the step that happened while offline

  // The hub comes back. Step the clock until SignalR's next reconnect attempt (0/2/10/30 s backoff, then the
  // app's 5 s restart loop) completes a handshake, then stop advancing time so no polling tick can interfere.
  state.hubDown = false;
  state.events = [...state.events, { sequence: 4, type: "HealthCheckPassed", message: "GET /health returned 200." }];
  const handshakes = state.handshakes;
  for (let second = 0; second < 90 && state.handshakes === handshakes; second++) {
    await page.clock.runFor(1_000);
    await page.waitForTimeout(100);
  }
  expect(state.handshakes).toBe(handshakes + 1);
  await expect(liveBadge(page)).toBeVisible();
  await expect(timeline(page)).toHaveCount(4);
  // Exactly one catch-up read of each view after the handshake.
  const atHandshake = state.hitsAtHandshake;
  await expect.poll(() => hits.events - atHandshake.events).toBe(1);
  await expect.poll(() => hits.approvals - atHandshake.approvals).toBe(1);
  await expect.poll(() => hits.list - atHandshake.list).toBe(1);
  await page.waitForTimeout(250);
  const afterCatchUp = { ...hits };
  expect(afterCatchUp.events - atHandshake.events).toBe(1);
  await page.clock.runFor(25_000);
  await page.waitForTimeout(250);
  expect(hits).toEqual(afterCatchUp);
});

test("intermediate Docker steps appear one by one from successive hub notifications", async ({ page }) => {
  await page.clock.install();
  const { state } = await mockApp(page);
  await signIn(page);
  await expect(liveBadge(page)).toBeVisible();
  await page.getByRole("button", { name: "Deployments", exact: true }).click();
  await page.getByRole("button", { name: /Deploying/ }).click();
  await expect(timeline(page)).toHaveCount(1);

  const steps: Array<[string, string]> = [
    ["ImagePulled", "example/app:1.0.0"],
    ["ContainerStarted", "web"],
    ["HealthCheckPassed", "GET /health returned 200."],
  ];
  for (const [type, message] of steps) {
    // The Worker commits one step and publishes one hint; nothing else is visible until the hint arrives.
    state.events = [...state.events, { sequence: state.events.length + 1, type, message }];
    await page.clock.runFor(5_000);
    await page.waitForTimeout(100);
    await expect(timeline(page)).toHaveCount(state.events.length - 1);
    state.socket?.send(deploymentChanged);
    await expect(timeline(page)).toHaveCount(state.events.length);
    await expect(timeline(page).last()).toHaveText(`${type}: ${message}`);
  }

  state.status = "Succeeded";
  state.events = [...state.events, { sequence: 5, type: "Succeeded", message: "Container is healthy." }];
  state.socket?.send(deploymentChanged);
  await expect(timeline(page)).toHaveText([
    "DeployStarted: Docker executor started for web.",
    "ImagePulled: example/app:1.0.0",
    "ContainerStarted: web",
    "HealthCheckPassed: GET /health returned 200.",
    "Succeeded: Container is healthy.",
  ]);
});
