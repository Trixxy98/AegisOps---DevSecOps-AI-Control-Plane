import { expect, test } from "@playwright/test";

// docs/09: an account with a temporary password must choose its own before the workspace opens.
// Mocked API: the first login returns a token whose /auth/me says mustChangePassword; the second login (with the new
// password) returns a normal token.
test("a temporary password must be changed before the workspace opens", async ({ page }) => {
  const logins: string[] = [];
  let changeBody: { currentPassword?: string; newPassword?: string } = {};
  await page.route("**/api/v1/auth/login", async (route) => {
    const body = route.request().postDataJSON() as { password: string };
    logins.push(body.password);
    await route.fulfill({
      json: {
        accessToken: logins.length === 1 ? "temporary-session" : "settled-session",
        expiresAt: "2030-01-01T00:00:00Z",
      },
    });
  });
  await page.route("**/api/v1/auth/me", (route) =>
    route.fulfill({
      json: {
        id: "00000000-0000-7000-8000-000000000001",
        email: "admin@aegisops.local",
        displayName: "Admin",
        mustChangePassword: route.request().headers()["authorization"] === "Bearer temporary-session",
        roles: [],
        permissions: [],
      },
    }),
  );
  await page.route("**/api/v1/auth/change-password", async (route) => {
    changeBody = route.request().postDataJSON() as typeof changeBody;
    await route.fulfill({ status: 204 });
  });
  await page.route("**/api/v1/deployments/approval-queue", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/deployments", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/system/dependencies", (route) =>
    route.fulfill({ json: { ollama: "Healthy", docker: "Healthy", worker: "Healthy" } }),
  );
  await page.route("**/hubs/ops/**", (route) => route.fulfill({ status: 503 }));

  await page.goto("/");
  await page.getByLabel("Password").fill("temporary-password-0001");
  await page.getByRole("button", { name: "Continue" }).click();

  await expect(page.getByRole("heading", { name: "Choose a new password" })).toBeVisible();
  await expect(page.getByRole("navigation", { name: "Main navigation" })).toHaveCount(0);

  await page.getByLabel("New password", { exact: true }).fill("short");
  await page.getByLabel("Confirm new password").fill("short");
  await page.getByRole("button", { name: "Set password and continue" }).click();
  await expect(page.getByRole("alert")).toContainText("at least 12 characters");

  await page.getByLabel("New password", { exact: true }).fill("a-much-better-password");
  await page.getByLabel("Confirm new password").fill("a-different-password!!");
  await page.getByRole("button", { name: "Set password and continue" }).click();
  await expect(page.getByRole("alert")).toContainText("do not match");

  await page.getByLabel("Confirm new password").fill("a-much-better-password");
  await page.getByRole("button", { name: "Set password and continue" }).click();

  await expect(page.getByRole("navigation", { name: "Main navigation" }).first()).toBeVisible();
  expect(changeBody).toEqual({ currentPassword: "temporary-password-0001", newPassword: "a-much-better-password" });
  expect(logins).toEqual(["temporary-password-0001", "a-much-better-password"]);
});
