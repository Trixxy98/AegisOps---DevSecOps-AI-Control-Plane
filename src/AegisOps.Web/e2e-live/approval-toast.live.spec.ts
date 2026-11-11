import { expect, request, test, type APIRequestContext, type Page } from "@playwright/test";

// No @types/node in this package: read the environment through a typed view of globalThis.process.
const env = (globalThis as { process?: { env: Record<string, string | undefined> } }).process?.env ?? {};

// P5-06 / P5-12 / P5-13 against the REAL Compose stack (API, Worker, PostgreSQL, Redis, SignalR) — nothing is mocked.
// Two isolated browser contexts: a developer watching Deployments and a Security approver watching Approvals.
// The developer requests a Production deployment; the Worker evaluates it to AwaitingApproval; the approver must get
// the toast within one second of that commit. One approval follows, then the developer cancels, so nothing executes.
//
// Safety: the request is only made when /policies/simulate says RequireApproval, and the deployment is cancelled in
// `finally`. Passwords come from environment variables set in the operator's own terminal and are never printed.
//
//   AEGISOPS_DEVELOPER_PASSWORD=… AEGISOPS_SECURITY_PASSWORD=… npm run test:e2e:live

const apiUrl = env.AEGISOPS_API_URL ?? "http://localhost:8080";
const project = env.AEGISOPS_LIVE_PROJECT ?? "circuit-service";
const developer = {
  email: env.AEGISOPS_DEVELOPER_EMAIL ?? "developer@aegisops.local",
  password: env.AEGISOPS_DEVELOPER_PASSWORD,
};
const security = {
  email: env.AEGISOPS_SECURITY_EMAIL ?? "security@aegisops.local",
  password: env.AEGISOPS_SECURITY_PASSWORD,
};

type Environment = { id: string; tier: string };
type Artifact = { id: string; version: string };
type TimelineEvent = { sequence: number; timestamp: string; type: string };

async function apiToken(api: APIRequestContext, email: string, password: string) {
  const response = await api.post("/api/v1/auth/login", { data: { email, password } });
  expect(response.status(), `API sign-in for ${email}`).toBe(200);
  return ((await response.json()) as { accessToken: string }).accessToken;
}

async function signIn(page: Page, email: string, password: string) {
  await page.goto("/");
  await page.getByLabel("Email address").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Continue" }).click();
  const changeRequired = page.getByRole("heading", { name: "Choose a new password" });
  await expect(page.getByText("Live updates").first().or(changeRequired)).toBeVisible({ timeout: 15_000 });
  if (await changeRequired.isVisible()) {
    throw new Error(`${email} still has a temporary password: sign in once in the web UI and choose a new one.`);
  }
  await expect(page.getByText("Live updates").first()).toBeVisible({ timeout: 15_000 });
}

/** Records (in the page, with the host clock) when a toast mentioning `text` first appears. */
async function watchToast(page: Page) {
  await page.evaluate(() => {
    const record = window as unknown as { __toasts: Array<{ text: string; at: number }> };
    record.__toasts = [];
    new MutationObserver(() => {
      for (const node of document.querySelectorAll(".deployment-toast")) {
        const text = node.textContent ?? "";
        if (!record.__toasts.some((toast) => toast.text === text)) record.__toasts.push({ text, at: Date.now() });
      }
    }).observe(document.body, { childList: true, subtree: true, characterData: true });
  });
}

test("approval toast and live timeline over the real stack", async ({ browser }) => {
  test.skip(
    !developer.password || !security.password,
    "Set AEGISOPS_DEVELOPER_PASSWORD and AEGISOPS_SECURITY_PASSWORD.",
  );
  test.setTimeout(120_000);

  const api = await request.newContext({ baseURL: apiUrl });
  const devToken = await apiToken(api, developer.email, developer.password!);
  const auth = { Authorization: `Bearer ${devToken}` };

  const detail = (await (await api.get(`/api/v1/projects/${project}`, { headers: auth })).json()) as {
    environments: Environment[];
  };
  const production = detail.environments.find((environment) => environment.tier === "Production");
  const artifacts = (await (
    await api.get(`/api/v1/projects/${project}/artifacts?branch=main`, { headers: auth })
  ).json()) as Artifact[];
  const artifact = artifacts[0];
  expect(production, "Production environment").toBeDefined();
  expect(artifact, "main artifact").toBeDefined();

  const simulated = (await (
    await api.post("/api/v1/policies/simulate", {
      headers: auth,
      data: { artifactId: artifact.id, environmentId: production!.id },
    })
  ).json()) as { decision: string };
  test.skip(
    simulated.decision !== "RequireApproval",
    `Production policy is ${simulated.decision} now; nothing requested.`,
  );

  // Contexts created here do not inherit `use.video`; record them explicitly (for the README GIF, P8-11).
  const recordVideo =
    env.AEGISOPS_LIVE_VIDEO === "1" ? { dir: "test-results/videos", size: { width: 1280, height: 800 } } : undefined;
  const devContext = await browser.newContext({ recordVideo, viewport: { width: 1280, height: 800 } });
  const securityContext = await browser.newContext({ recordVideo, viewport: { width: 1280, height: 800 } });
  const devPage = await devContext.newPage();
  const securityPage = await securityContext.newPage();
  // Demo mode (video on): slow down between steps so the recording is watchable, and save README screenshots.
  // Timing assertions are taken before any pause, so they are unaffected.
  const demo = env.AEGISOPS_LIVE_VIDEO === "1";
  const pause = (ms = 2_500) => (demo ? new Promise((resolve) => setTimeout(resolve, ms)) : Promise.resolve());
  const shot = async (page: Page, name: string) => {
    if (demo) await page.screenshot({ path: `test-results/screenshots/${name}.png` });
  };
  let deploymentId: string | undefined;
  try {
    await signIn(devPage, developer.email, developer.password!);
    await signIn(securityPage, security.email, security.password!);
    await devPage.getByRole("button", { name: "Deployments", exact: true }).click();
    await securityPage.getByRole("button", { name: "Approvals", exact: true }).click();
    await watchToast(securityPage);
    await pause(3_000);

    const requestedAt = Date.now();
    const created = await api.post("/api/v1/deployments", {
      headers: { ...auth, "Idempotency-Key": crypto.randomUUID() },
      data: {
        artifactId: artifact.id,
        environmentId: production!.id,
        reason: "P5 live toast check; cancelled afterwards",
      },
    });
    expect(created.status()).toBe(202);
    deploymentId = ((await created.json()) as { id: string }).id;

    // Approver: toast for this deployment, measured from the commit of ApprovalRequested.
    await expect(securityPage.getByRole("status").filter({ hasText: deploymentId })).toBeVisible({ timeout: 15_000 });
    const toastAt = await securityPage.evaluate(
      (id) =>
        (window as unknown as { __toasts: Array<{ text: string; at: number }> }).__toasts.find((toast) =>
          toast.text.includes(id),
        )?.at,
      deploymentId,
    );
    const events = (await (
      await api.get(`/api/v1/deployments/${deploymentId}/events`, { headers: auth })
    ).json()) as TimelineEvent[];
    const awaiting = events.find((event) => event.type === "ApprovalRequested");
    expect(awaiting, "ApprovalRequested event").toBeDefined();
    const latency = toastAt! - Date.parse(awaiting!.timestamp);
    console.log(
      `toast ${latency} ms after ApprovalRequested was committed; ${toastAt! - requestedAt} ms after the request`,
    );
    expect(latency).toBeLessThan(1_000);
    await shot(securityPage, "approval-toast");
    await pause();

    // Developer: the list updates without a reload.
    const latest = devPage.locator(".deployment-list-button").first();
    await expect(latest).toContainText("AwaitingApproval", { timeout: 5_000 });
    await expect(latest).toContainText("Approvals 0/2");
    if (demo) {
      await latest.click(); // show the policy explanation for the README
      await pause(1_000);
      await shot(devPage, "deployment-awaiting-approval");
      await pause(3_000);
    }

    // One approval in the approver's UI; the developer's list follows live.
    await securityPage
      .locator("li", { hasText: deploymentId })
      .getByRole("button", { name: "Approve", exact: true })
      .click();
    await expect(latest).toContainText("Approvals 1/2", { timeout: 5_000 });
    await shot(devPage, "deployment-one-approval");
    await pause(3_000);
  } finally {
    if (deploymentId) {
      const cancelled = await api.post(`/api/v1/deployments/${deploymentId}/cancel`, { headers: auth });
      expect([204, 409]).toContain(cancelled.status());
    }
  }
  await expect(devPage.locator(".deployment-list-button").first()).toContainText("Cancelled", { timeout: 5_000 });
  await pause(2_500);
  await devContext.close();
  await securityContext.close();
  if (demo) {
    // Stable names for scripts/make-demo-gif.sh (videos are finalised when their context closes).
    await devPage.video()?.saveAs("test-results/videos/developer.webm");
    await securityPage.video()?.saveAs("test-results/videos/approver.webm");
  }
  await api.dispose();
});
