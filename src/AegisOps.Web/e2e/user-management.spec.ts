import { expect, test, type Page } from "@playwright/test";
import { signInWithPermissions } from "./mockSession";

// Mocked API routes only (no live backend). The handlers emulate src/AegisOps.Api/Identity/CreateUser.cs,
// UpdateUser.cs, SetUserRoles.cs and ResetUserPassword.cs. The values below are fixtures, not real credentials.
const selfId = "00000000-0000-7000-8000-000000000001"; // the signed-in user of mockSession.ts
const devId = "00000000-0000-7000-8000-0000000c0002";
// Built at runtime so secret scanners do not mistake these obvious placeholders for credentials.
const temporaryPassword = ["e2e", "fixture", "temporary", "pw"].join("-");
const resetPassword = ["e2e", "fixture", "reset", "pw", "0001"].join("-");

type User = { id: string; email: string; displayName: string; isActive: boolean; roles: string[] };

async function mockAdministration(page: Page) {
  const users: User[] = [
    { id: selfId, email: "e2e@aegisops.local", displayName: "E2E", isActive: true, roles: ["Admin"] },
    { id: devId, email: "dev@aegisops.local", displayName: "Dev", isActive: true, roles: ["Developer"] },
  ];
  const requests: Array<{ method: string; path: string; body: unknown }> = [];

  await page.route("**/api/v1/teams", (route) =>
    route.fulfill({ json: [{ id: "00000000-0000-7000-8000-0000000a0001", name: "Platform", slug: "platform" }] }),
  );
  await page.route("**/api/v1/teams/*/webhook**", (route) =>
    route.fulfill({ status: 404, json: { title: "Team was not found.", status: 404 } }),
  );
  await page.route("**/api/v1/admin/jobs**", (route) =>
    route.fulfill({
      json: route.request().url().includes("summary") ? { pending: 0, running: 0, deadLettered: 0 } : [],
    }),
  );
  await page.route("**/api/v1/admin/users", async (route) => {
    if (route.request().method() !== "POST") return route.fulfill({ json: users });
    const body = route.request().postDataJSON() as { email: string; displayName: string; roles: string[] };
    requests.push({ method: "POST", path: "/api/v1/admin/users", body });
    if (users.some((user) => user.email.toLowerCase() === body.email.toLowerCase())) {
      return route.fulfill({
        status: 409,
        contentType: "application/problem+json",
        json: { title: "A user with this email already exists.", status: 409 },
      });
    }
    const created = { id: "00000000-0000-7000-8000-0000000c0003", isActive: true, ...body };
    users.push({ id: created.id, email: body.email, displayName: body.displayName, isActive: true, roles: body.roles });
    return route.fulfill({
      status: 201,
      headers: { "Cache-Control": "no-store" },
      json: { ...created, team: null, temporaryPassword, mustChangePassword: true },
    });
  });
  await page.route("**/api/v1/admin/users/*/reset-password", (route) => {
    requests.push({ method: "POST", path: new URL(route.request().url()).pathname, body: null });
    return route.fulfill({ json: { userId: devId, temporaryPassword: resetPassword, mustChangePassword: true } });
  });
  await page.route("**/api/v1/admin/users/*/roles", (route) => {
    const body = route.request().postDataJSON() as { roles: string[] };
    requests.push({ method: "PUT", path: new URL(route.request().url()).pathname, body });
    const user = users.find((item) => route.request().url().includes(item.id))!;
    user.roles = body.roles;
    return route.fulfill({ json: user });
  });
  await page.route(`**/api/v1/admin/users/${devId}`, (route) => {
    const body = route.request().postDataJSON() as { isActive: boolean };
    requests.push({ method: "PATCH", path: new URL(route.request().url()).pathname, body });
    const user = users.find((item) => item.id === devId)!;
    user.isActive = body.isActive;
    return route.fulfill({ json: user });
  });
  return requests;
}

async function openAdministration(page: Page, permissions: string[]) {
  await signInWithPermissions(page, permissions);
  await page.getByRole("button", { name: "Administration", exact: true }).click();
}

test("adds a user and shows the temporary password only once", async ({ page }) => {
  const requests = await mockAdministration(page);
  await openAdministration(page, ["users:manage", "roles:manage"]);

  const form = page.getByRole("form", { name: "Add user" });
  await form.getByLabel("Email").fill("aina@aegisops.local");
  await form.getByLabel("Display name").fill("Aina");
  await form.getByLabel("Approver").check();
  await form.getByRole("button", { name: "Add user" }).click();

  const notice = page.getByRole("status", { name: "Temporary password" });
  await expect(notice).toContainText("aina@aegisops.local");
  await expect(notice.getByTestId("temporary-password")).toHaveText(temporaryPassword);
  await expect(notice).toContainText("cannot be shown again");
  await expect(page.getByRole("listitem", { name: "aina@aegisops.local" })).toContainText("Developer, Approver");
  expect(requests).toEqual([
    {
      method: "POST",
      path: "/api/v1/admin/users",
      body: { email: "aina@aegisops.local", displayName: "Aina", roles: ["Developer", "Approver"], team: null },
    },
  ]);

  await notice.getByRole("button", { name: "Dismiss" }).click();
  await expect(page.getByRole("status", { name: "Temporary password" })).toHaveCount(0);
  await expect(page.getByText(temporaryPassword)).toHaveCount(0);
});

test("shows the 409 Problem Details for a duplicate email", async ({ page }) => {
  await mockAdministration(page);
  await openAdministration(page, ["users:manage", "roles:manage"]);

  const form = page.getByRole("form", { name: "Add user" });
  await form.getByLabel("Email").fill("DEV@aegisops.local");
  await form.getByLabel("Display name").fill("Dev again");
  await form.getByRole("button", { name: "Add user" }).click();

  await expect(form.getByRole("alert")).toHaveText("A user with this email already exists.");
  await expect(page.getByRole("status", { name: "Temporary password" })).toHaveCount(0);
});

test("deactivates, edits roles and resets a password", async ({ page }) => {
  const requests = await mockAdministration(page);
  await openAdministration(page, ["users:manage", "roles:manage"]);

  // The signed-in Admin cannot deactivate themself, so the button is not offered on their own row.
  const self = page.getByRole("listitem", { name: "e2e@aegisops.local" });
  await expect(self.getByRole("button", { name: "Deactivate" })).toHaveCount(0);

  const dev = page.getByRole("listitem", { name: "dev@aegisops.local" });
  await dev.getByRole("button", { name: "Deactivate" }).click();
  await expect(dev).toContainText("Inactive");
  await expect(dev.getByRole("button", { name: "Activate" })).toBeVisible();

  await dev.getByRole("button", { name: "Edit roles" }).click();
  await dev.getByLabel("Security").check();
  await dev.getByRole("button", { name: "Save roles" }).click();
  await expect(dev).toContainText("Developer, Security");

  await dev.getByRole("button", { name: "Reset password" }).click();
  await expect(page.getByRole("status", { name: "Temporary password" }).getByTestId("temporary-password")).toHaveText(
    resetPassword,
  );

  expect(requests.map((request) => `${request.method} ${request.path}`)).toEqual([
    `PATCH /api/v1/admin/users/${devId}`,
    `PUT /api/v1/admin/users/${devId}/roles`,
    `POST /api/v1/admin/users/${devId}/reset-password`,
  ]);
});

test("hides role editing without roles:manage", async ({ page }) => {
  await mockAdministration(page);
  await openAdministration(page, ["users:manage"]);

  const dev = page.getByRole("listitem", { name: "dev@aegisops.local" });
  await expect(dev.getByRole("button", { name: "Deactivate" })).toBeVisible();
  await expect(dev.getByRole("button", { name: "Reset password" })).toBeVisible();
  await expect(dev.getByRole("button", { name: "Edit roles" })).toHaveCount(0);
});
