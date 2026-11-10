import type { Page } from "@playwright/test";

/**
 * Mocks a signed-in session: login, /auth/me with the given permissions, the dashboard's background calls and
 * the SignalR hub. Each spec adds the routes for the feature it exercises. No real backend is involved.
 */
export async function signInWithPermissions(page: Page, permissions: string[]) {
  await page.route("**/api/v1/auth/login", (route) =>
    route.fulfill({ json: { accessToken: "e2e-session", expiresAt: "2030-01-01T00:00:00Z" } }),
  );
  await page.route("**/api/v1/auth/me", (route) =>
    route.fulfill({
      json: {
        id: "00000000-0000-7000-8000-000000000001",
        email: "e2e@aegisops.local",
        displayName: "E2E",
        mustChangePassword: false,
        roles: [],
        permissions,
      },
    }),
  );
  await page.route("**/api/v1/deployments/approval-queue", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/deployments", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/system/dependencies", (route) =>
    route.fulfill({ json: { ollama: "Healthy", docker: "Healthy", worker: "Healthy" } }),
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
}
