import { expect, test, type Page } from "@playwright/test";
import { signInWithPermissions } from "./mockSession";

// Mocked API routes only (no live backend). POST /api/v1/projects emulates
// src/AegisOps.Api/Organization/CreateProject.cs: 201 with the new project, 409 Problem Details for a taken slug.
const teams = [
  { id: "00000000-0000-7000-8000-0000000a0001", name: "Platform", slug: "platform", myRole: "Owner" },
  { id: "00000000-0000-7000-8000-0000000a0002", name: "Payments", slug: "payments", myRole: "Member" },
];

async function mockProjects(page: Page) {
  const projects = [{ slug: "circuit-service", name: "Circuit service" }];
  const posts: unknown[] = [];

  await page.route("**/api/v1/teams", (route) => route.fulfill({ json: teams }));
  await page.route("**/api/v1/projects", async (route) => {
    if (route.request().method() !== "POST") {
      return route.fulfill({ json: projects });
    }
    const body = route.request().postDataJSON() as { teamId: string; name: string; slug: string };
    posts.push(body);
    if (projects.some((project) => project.slug === body.slug)) {
      return route.fulfill({
        status: 409,
        contentType: "application/problem+json",
        json: { title: "A project with this slug already exists.", status: 409 },
      });
    }
    projects.push({ slug: body.slug, name: body.name });
    return route.fulfill({
      status: 201,
      json: {
        id: "00000000-0000-7000-8000-0000000b0001",
        teamId: body.teamId,
        name: body.name,
        slug: body.slug,
        description: null,
        isArchived: false,
        environments: [],
        repository: null,
      },
    });
  });
  await page.route("**/api/v1/projects/orders-service", (route) =>
    route.fulfill({
      json: {
        environments: [
          {
            id: "00000000-0000-7000-8000-0000000e0002",
            name: "Staging",
            tier: "Staging",
            targetType: "noop",
            hostPort: 0,
            containerPort: 0,
            healthTimeoutSeconds: 0,
          },
        ],
      },
    }),
  );
  await page.route("**/api/v1/projects/orders-service/artifacts", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/projects/orders-service/api-keys", (route) => route.fulfill({ json: [] }));
  await page.route("**/api/v1/projects/orders-service/environments/*/variables", (route) =>
    route.fulfill({ json: [] }),
  );
  return posts;
}

test("creates a project in an owned team and selects it", async ({ page }) => {
  const posts = await mockProjects(page);
  await signInWithPermissions(page, ["projects:read", "projects:write"]);
  await page.getByRole("button", { name: "Projects", exact: true }).click();

  const form = page.getByRole("form", { name: "Create project" });
  // Only teams the user owns are offered (Payments is Member-only).
  await expect(form.getByLabel("Team").locator("option")).toHaveText(["Platform"]);
  await form.getByLabel("Name").fill("Orders Service");
  await expect(form.getByLabel("Slug")).toHaveValue("orders-service");
  await expect(form).toContainText("Lowercase letters, digits and hyphens");
  await form.getByLabel("Description (optional)").fill("Takes orders");
  await form.getByRole("button", { name: "Create project" }).click();

  await expect(page.getByRole("heading", { name: "orders-service" })).toBeVisible();
  await expect(page.getByRole("button", { name: /Orders Service/ })).toHaveClass(/is-selected/);
  expect(posts).toEqual([
    { teamId: teams[0].id, name: "Orders Service", slug: "orders-service", description: "Takes orders" },
  ]);
});

test("shows the Problem Details when the slug is taken", async ({ page }) => {
  await mockProjects(page);
  await signInWithPermissions(page, ["projects:read", "projects:write"]);
  await page.getByRole("button", { name: "Projects", exact: true }).click();

  const form = page.getByRole("form", { name: "Create project" });
  await form.getByLabel("Name").fill("Circuit service");
  await form.getByRole("button", { name: "Create project" }).click();

  await expect(form.getByRole("alert")).toHaveText("A project with this slug already exists.");
  await expect(page.getByRole("heading", { name: "circuit-service" })).toHaveCount(0);
});

test("hides the form without projects:write", async ({ page }) => {
  await mockProjects(page);
  await signInWithPermissions(page, ["projects:read"]);
  await page.getByRole("button", { name: "Projects", exact: true }).click();

  await expect(page.getByRole("button", { name: /Circuit service/ })).toBeVisible();
  await expect(page.getByRole("form", { name: "Create project" })).toHaveCount(0);
});
