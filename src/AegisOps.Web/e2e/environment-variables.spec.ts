import { expect, test, type Page, type Route } from "@playwright/test";
import { signInWithPermissions } from "./mockSession";

// Mocked API routes only (no live backend). The variables routes emulate
// src/AegisOps.Api/Organization/EnvironmentVariableEndpoints.cs: secret values are write-only (always null).
type MockVariable = { name: string; isSecret: boolean; value: string | null; updatedAt: string };

const slug = "site-service";
const environmentId = "00000000-0000-7000-8000-0000000e0001";
const variablesPath = `/api/v1/projects/${slug}/environments/${environmentId}/variables`;

async function mockProject(page: Page, secretsConfigured: boolean) {
  const variables: MockVariable[] = [
    {
      name: "ConnectionStrings__Default",
      isSecret: false,
      value: "Host=db;Database=site",
      updatedAt: "2026-11-11T09:00:00Z",
    },
    { name: "DB_PASSWORD", isSecret: true, value: null, updatedAt: "2026-11-11T09:00:00Z" },
  ];
  const puts: Array<{ name: string; body: unknown }> = [];

  await page.route("**/api/v1/projects", (route) => route.fulfill({ json: [{ slug, name: "Site service" }] }));
  await page.route(`**/api/v1/projects/${slug}`, (route) =>
    route.fulfill({
      json: {
        environments: [
          {
            id: environmentId,
            name: "Staging",
            tier: "Staging",
            targetType: "docker",
            containerName: "site-staging",
            network: "aegisops-staging",
            hostPort: 8102,
            containerPort: 8080,
            healthPath: "/health",
            healthTimeoutSeconds: 60,
          },
        ],
      },
    }),
  );
  await page.route(`**/api/v1/projects/${slug}/artifacts`, (route) => route.fulfill({ json: [] }));
  await page.route(`**/api/v1/projects/${slug}/api-keys`, (route) => route.fulfill({ json: [] }));
  await page.route(`**${variablesPath}`, (route) => route.fulfill({ json: variables }));
  await page.route(`**${variablesPath}/*`, async (route: Route) => {
    const name = decodeURIComponent(new URL(route.request().url()).pathname.split("/").pop() ?? "");
    if (route.request().method() === "DELETE") {
      variables.splice(
        variables.findIndex((item) => item.name === name),
        1,
      );
      return route.fulfill({ status: 204 });
    }
    const body = route.request().postDataJSON() as { value: string; isSecret: boolean };
    puts.push({ name, body });
    if (body.isSecret && !secretsConfigured) {
      return route.fulfill({
        status: 409,
        contentType: "application/problem+json",
        json: {
          title: "Secret storage is not configured.",
          detail:
            "Set EnvironmentSecrets:Enabled and an encryption key (scripts/gen-secrets.sh) to store secret variables.",
          status: 409,
        },
      });
    }
    const saved = {
      name,
      isSecret: body.isSecret,
      value: body.isSecret ? null : body.value,
      updatedAt: "2026-11-11T10:00:00Z",
    };
    variables.push(saved);
    return route.fulfill({ status: 201, json: saved });
  });
  return puts;
}

async function openProject(page: Page) {
  await page.getByRole("button", { name: "Projects", exact: true }).click();
  await page.getByRole("button", { name: /Site service/ }).click();
}

test("lists variables with masked secrets and saves a write-only secret", async ({ page }) => {
  const puts = await mockProject(page, true);
  await signInWithPermissions(page, ["projects:read", "environments:write"]);
  await openProject(page);

  const list = page.getByRole("list", { name: "Staging variable list" });
  await expect(list.getByRole("listitem")).toHaveCount(2);
  await expect(list).toContainText("Host=db;Database=site");
  await expect(list.getByRole("listitem").filter({ hasText: "DB_PASSWORD" })).toContainText("••••• (secret)");

  await page.getByLabel("Variable name").fill("API_TOKEN");
  await page.getByLabel("Value", { exact: true }).fill("super-secret-token");
  await page.getByLabel("Secret (write-only, encrypted)").check();
  await page.getByRole("button", { name: "Save variable" }).click();

  await expect(list.getByRole("listitem")).toHaveCount(3);
  await expect(list.getByRole("listitem").filter({ hasText: "API_TOKEN" })).toContainText("••••• (secret)");
  await expect(page.getByText("super-secret-token")).toHaveCount(0);
  expect(puts).toEqual([{ name: "API_TOKEN", body: { value: "super-secret-token", isSecret: true } }]);

  await page.getByRole("button", { name: "Delete ConnectionStrings__Default" }).click();
  await expect(list.getByRole("listitem")).toHaveCount(2);
});

test("shows the Problem Details when secret storage is not configured", async ({ page }) => {
  await mockProject(page, false);
  await signInWithPermissions(page, ["projects:read", "environments:write"]);
  await openProject(page);

  await page.getByLabel("Variable name").fill("API_TOKEN");
  await page.getByLabel("Value", { exact: true }).fill("super-secret-token");
  await page.getByLabel("Secret (write-only, encrypted)").check();
  await page.getByRole("button", { name: "Save variable" }).click();

  const group = page.getByRole("group", { name: "Staging variables" });
  await expect(group.getByRole("alert")).toContainText("Secret storage is not configured.");
  await expect(group.getByRole("listitem")).toHaveCount(2);
});

test("read-only users see variables without edit controls", async ({ page }) => {
  await mockProject(page, true);
  await signInWithPermissions(page, ["projects:read"]);
  await openProject(page);

  await expect(page.getByRole("list", { name: "Staging variable list" }).getByRole("listitem")).toHaveCount(2);
  await expect(page.getByRole("button", { name: "Save variable" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /^Delete / })).toHaveCount(0);
});
