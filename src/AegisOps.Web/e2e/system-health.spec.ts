import { expect, test } from "@playwright/test";

test("signed-in workspace shows Ollama and Docker degradation", async ({ page }) => {
  await page.route("**/api/v1/auth/login", (route) =>
    route.fulfill({
      json: { accessToken: "e2e-session", expiresAt: "2030-01-01T00:00:00Z" },
    }),
  );
  await page.route("**/api/v1/deployments/approval-queue", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/deployments", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/system/dependencies", (route) =>
    route.fulfill({
      json: { ollama: "Degraded", docker: "Degraded", worker: "Healthy" },
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
  await expect(page.getByRole("status").filter({ hasText: "Ollama unavailable" })).toBeVisible();
  await expect(page.getByRole("status").filter({ hasText: "Docker unavailable" })).toBeVisible();
});

test("signed-in workspace warns when worker is unhealthy but Docker responds", async ({ page }) => {
  await page.route("**/api/v1/auth/login", (route) =>
    route.fulfill({
      json: { accessToken: "e2e-session", expiresAt: "2030-01-01T00:00:00Z" },
    }),
  );
  await page.route("**/api/v1/deployments/approval-queue", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/deployments", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/system/dependencies", (route) =>
    route.fulfill({
      json: { ollama: "Healthy", docker: "Healthy", worker: "Unhealthy" },
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
  await expect(page.getByRole("status").filter({ hasText: "Worker unhealthy" })).toBeVisible();
});
