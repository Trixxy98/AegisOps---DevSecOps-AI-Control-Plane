import { expect, test } from "@playwright/test";

const deploymentChanged = JSON.stringify({
  type: 1,
  target: "DeploymentChanged",
  arguments: ["00000000-0000-7000-8000-000000000001"],
}) + "\u001e";

test("both signed-in browser sessions refresh after a deployment update", async ({ browser }) => {
  let deployments = [{ status: "Succeeded" }];
  const sendUpdate = new Array<(() => void) | undefined>(2);
  const contexts = await Promise.all([browser.newContext(), browser.newContext()]);

  try {
    const pages = await Promise.all(contexts.map(async (context, index) => {
      await context.route("**/api/v1/auth/login", (route) => route.fulfill({
        json: { accessToken: `e2e-session-${index}`, expiresAt: "2030-01-01T00:00:00Z" },
      }));
      await context.route("**/api/v1/deployments/approval-queue", (route) => route.fulfill({ json: [] }));
      await context.route("**/api/v1/deployments", (route) => route.fulfill({ json: deployments }));
      await context.route("**/hubs/ops/negotiate*", (route) => route.fulfill({
        json: {
          connectionId: `e2e-connection-${index}`,
          connectionToken: `e2e-connection-token-${index}`,
          negotiateVersion: 1,
          availableTransports: [{ transport: "WebSockets", transferFormats: ["Text"] }],
        },
      }));
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
    }));

    for (const page of pages) {
      await expect(page.getByRole("status").filter({ hasText: "Live updates" })).toBeVisible();
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
