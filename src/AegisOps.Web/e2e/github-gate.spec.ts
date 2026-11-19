import { expect, test, type Page } from "@playwright/test";
import { mockSession, signIn } from "./mockSession";

// Mocked API only. The shapes follow DeploymentEndpoints.List/Get (source, gitHubRunUrl, gitHub) and
// UpdateEnvironment (target type "github") in src/AegisOps.Api; see docs/github-deployment-gate.md.
const gitHubDeployment = "00000000-0000-7000-8000-0000000a0001";
const uiDeployment = "00000000-0000-7000-8000-0000000a0002";
const runUrl = "https://github.com/acme/circuit-service/actions/runs/4242";

async function mockDeployments(page: Page, gate: Record<string, unknown>) {
  await page.route("**/api/v1/deployments", (route) =>
    route.fulfill({
      json: [
        {
          id: gitHubDeployment,
          status: "AwaitingApproval",
          approvalsReceived: 0,
          approvalsRequired: 1,
          requestedAt: "2026-11-19T09:00:00Z",
          source: "GitHub",
          gitHubRunUrl: runUrl,
        },
        {
          id: uiDeployment,
          status: "Succeeded",
          approvalsReceived: 0,
          approvalsRequired: 0,
          requestedAt: "2026-11-18T09:00:00Z",
          source: "AegisOps",
          gitHubRunUrl: null,
        },
      ],
    }),
  );
  await page.route(`**/api/v1/deployments/${gitHubDeployment}`, (route) =>
    route.fulfill({
      json: {
        id: gitHubDeployment,
        artifactId: "00000000-0000-7000-8000-0000000a0003",
        status: "AwaitingApproval",
        source: "GitHub",
        reason: `GitHub Actions run ${runUrl}`,
        gitHub: {
          repository: "acme/circuit-service",
          environment: "production",
          runUrl,
          status: "Linked",
          decision: null,
          reportedAt: null,
          ...gate,
        },
      },
    }),
  );
  await page.route(`**/api/v1/deployments/${gitHubDeployment}/events`, (route) =>
    route.fulfill({
      json: [
        {
          sequence: 1,
          type: "Requested",
          message: `Requested by the GitHub deployment protection rule for acme/circuit-service (production), run ${runUrl}.`,
        },
      ],
    }),
  );
  await page.route(`**/api/v1/deployments/${gitHubDeployment}/evaluation`, (route) =>
    route.fulfill({ json: { decision: "RequireApproval", ruleResults: [] } }),
  );
  await page.route("**/api/v1/artifacts/*/findings", (route) => route.fulfill({ json: [] }));
}

test("GitHub-gated deployments show a GitHub badge and link to the workflow run", async ({ page }) => {
  await mockSession(page, ["deployments:read"]);
  await mockDeployments(page, {});
  await signIn(page);
  await page.getByRole("button", { name: "Deployments", exact: true }).click();

  const gitHubRow = page.getByRole("button", { name: /Awaiting approval/ });
  await expect(gitHubRow.getByText("GitHub", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: /Succeeded/ }).getByText("GitHub", { exact: true })).toHaveCount(0);

  await gitHubRow.click();
  const link = page.getByRole("link", { name: "Open workflow run" });
  await expect(link).toHaveAttribute("href", runUrl);
  await expect(link).toHaveAttribute("rel", "noopener noreferrer");
  await expect(page.getByText("acme/circuit-service", { exact: true })).toBeVisible();
  await expect(page.getByText("Waiting for the AegisOps decision")).toBeVisible();
});

test("the GitHub answer is shown once AegisOps has reported it", async ({ page }) => {
  await mockSession(page, ["deployments:read"]);
  await mockDeployments(page, { status: "Reported", decision: "rejected", reportedAt: "2026-11-19T09:05:00Z" });
  await signIn(page);
  await page.getByRole("button", { name: "Deployments", exact: true }).click();
  await page.getByRole("button", { name: /Awaiting approval/ }).click();

  await expect(page.getByText("Rejected on GitHub")).toBeVisible();
});

test("a non-https run URL is not rendered as a link", async ({ page }) => {
  await mockSession(page, ["deployments:read"]);
  await mockDeployments(page, { runUrl: "javascript:alert(1)" });
  await signIn(page);
  await page.getByRole("button", { name: "Deployments", exact: true }).click();
  await page.getByRole("button", { name: /Awaiting approval/ }).click();

  await expect(page.getByText("Workflow run link unavailable")).toBeVisible();
  await expect(page.getByRole("link", { name: "Open workflow run" })).toHaveCount(0);
});

test("an environment target can be set to github", async ({ page }) => {
  const slug = "circuit-service";
  const environmentId = "00000000-0000-7000-8000-0000000e0009";
  const patches: unknown[] = [];
  await mockSession(page, ["projects:read", "environments:write"]);
  await page.route("**/api/v1/projects", (route) => route.fulfill({ json: [{ slug, name: "Circuit service" }] }));
  await page.route(`**/api/v1/projects/${slug}`, (route) =>
    route.fulfill({
      json: {
        environments: [
          {
            id: environmentId,
            name: "Production",
            tier: "Production",
            targetType: "noop",
            hostPort: 0,
            containerPort: 0,
            healthTimeoutSeconds: 0,
          },
        ],
      },
    }),
  );
  await page.route(`**/api/v1/projects/${slug}/artifacts`, (route) => route.fulfill({ json: [] }));
  await page.route(`**/api/v1/projects/${slug}/api-keys`, (route) => route.fulfill({ json: [] }));
  await page.route(`**/api/v1/projects/${slug}/environments/${environmentId}/variables`, (route) =>
    route.fulfill({ json: [] }),
  );
  await page.route(`**/api/v1/projects/${slug}/environments/${environmentId}`, async (route) => {
    patches.push(route.request().postDataJSON());
    await route.fulfill({ json: {} });
  });
  await signIn(page);
  await page.getByRole("button", { name: "Projects", exact: true }).click();
  await page.getByRole("button", { name: /Circuit service/ }).click();

  const form = page.getByRole("form", { name: "Production target" });
  await form.getByLabel("Target type").selectOption("github");
  await form.getByRole("button", { name: "Save target" }).click();

  await expect.poll(() => patches.length).toBe(1);
  expect((patches[0] as { target: { type: string } }).target.type).toBe("github");
});
