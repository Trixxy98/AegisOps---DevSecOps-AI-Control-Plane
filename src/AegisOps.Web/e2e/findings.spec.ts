import { expect, test, type Page } from "@playwright/test";
import { signInWithPermissions } from "./mockSession";

// Mocked API routes only (no live backend). The findings route emulates the filters and paging of
// GET /api/v1/projects/{slug}/findings (src/AegisOps.Api/Organization/ListProjectFindings.cs).
type MockFinding = Record<string, unknown> & {
  id: string;
  severity: string;
  scanner: string;
  status: string;
  artifactId: string;
  fingerprint: string;
};

const artifactOld = "00000000-0000-7000-8000-0000000a0001";
const artifactNew = "00000000-0000-7000-8000-0000000a0002";

function fixtures(): MockFinding[] {
  const base = {
    scanId: "00000000-0000-7000-8000-0000000b0001",
    description: null,
    filePath: null,
    startLine: null,
    packageName: null,
    installedVersion: null,
    fixedVersion: null,
    cve: null,
    helpUri: null,
    suppressionReason: null,
    suppression: null,
  };
  return [
    {
      ...base,
      id: "00000000-0000-7000-8000-0000000f0001",
      artifactId: artifactOld,
      artifactVersion: "1.0.0",
      scanner: "Trivy",
      severity: "Critical",
      ruleId: "CVE-2024-0001",
      title: "OpenSSL buffer overflow",
      description: "A crafted certificate can overflow a buffer.",
      filePath: "Dockerfile",
      startLine: 12,
      packageName: "openssl",
      installedVersion: "3.0.1",
      fixedVersion: "3.0.9",
      cve: "CVE-2024-0001",
      helpUri: "https://avd.aquasec.com/nvd/cve-2024-0001",
      fingerprint: "fp-openssl",
      status: "Open",
    },
    {
      ...base,
      id: "00000000-0000-7000-8000-0000000f0002",
      artifactId: artifactNew,
      artifactVersion: "1.1.0",
      scanner: "Semgrep",
      severity: "High",
      ruleId: "csharp.sqli",
      title: "SQL injection",
      filePath: "src/Repo.cs",
      startLine: 42,
      fingerprint: "fp-sqli",
      status: "Open",
    },
    {
      ...base,
      id: "00000000-0000-7000-8000-0000000f0003",
      artifactId: artifactNew,
      artifactVersion: "1.1.0",
      scanner: "Gitleaks",
      severity: "Medium",
      ruleId: "generic-api-key",
      title: "Generic API key",
      filePath: "appsettings.json",
      startLine: 3,
      fingerprint: "fp-key",
      status: "Suppressed",
      suppressionReason: "Test fixture key",
      suppression: { reason: "Test fixture key", createdAt: "2026-11-01T00:00:00Z", expiresAt: null },
    },
  ];
}

async function mockFindingsApi(page: Page, permissions: string[]) {
  const findings = fixtures();
  const requests: URL[] = [];
  const suppressions: Array<Record<string, unknown>> = [];
  let rejectNextSuppression = false;

  await page.route("**/api/v1/projects", (route) =>
    route.fulfill({ json: [{ slug: "circuit-service", name: "Circuit service" }] }),
  );
  await page.route("**/api/v1/projects/circuit-service/artifacts", (route) =>
    route.fulfill({
      json: [
        { id: artifactNew, version: "1.1.0", branch: "main" },
        { id: artifactOld, version: "1.0.0", branch: "main" },
      ],
    }),
  );
  await page.route(
    (url) => url.pathname === "/api/v1/projects/circuit-service/findings",
    (route) => {
      const url = new URL(route.request().url());
      requests.push(url);
      const params = url.searchParams;
      const matching = findings.filter(
        (item) =>
          (!params.get("severity") || item.severity === params.get("severity")) &&
          (!params.get("scanner") || item.scanner === params.get("scanner")) &&
          (!params.get("status") || item.status === params.get("status")) &&
          (!params.get("artifactId") || item.artifactId === params.get("artifactId")),
      );
      const page = Number(params.get("page") ?? "1");
      const size = Number(params.get("pageSize") ?? "50");
      return route.fulfill({
        json: { page, pageSize: size, total: matching.length, items: matching.slice((page - 1) * size, page * size) },
      });
    },
  );
  await page.route("**/api/v1/projects/circuit-service/finding-suppressions", (route) => {
    const body = route.request().postDataJSON() as { fingerprint: string; reason: string; expiresAt: string | null };
    if (rejectNextSuppression) {
      rejectNextSuppression = false;
      return route.fulfill({
        status: 400,
        contentType: "application/problem+json",
        body: JSON.stringify({ title: "Suppression expiry must be in the future.", status: 400 }),
      });
    }
    suppressions.push(body);
    for (const item of findings.filter((finding) => finding.fingerprint === body.fingerprint)) {
      item.status = "Suppressed";
      item.suppressionReason = body.reason;
      item.suppression = { reason: body.reason, createdAt: "2026-11-10T00:00:00Z", expiresAt: body.expiresAt };
    }
    return route.fulfill({ json: { id: "00000000-0000-7000-8000-0000000c0001", fingerprint: body.fingerprint } });
  });

  await signInWithPermissions(page, permissions);
  await page.getByRole("button", { name: "Findings", exact: true }).click();
  return {
    requests,
    suppressions,
    rejectNext: () => {
      rejectNextSuppression = true;
    },
  };
}

test("findings explorer lists project findings and filters by severity, scanner, status and artifact", async ({
  page,
}) => {
  const api = await mockFindingsApi(page, ["findings:read"]);
  const list = page.getByRole("list", { name: "Findings list" });

  await expect(list.getByRole("button")).toHaveCount(3);
  await expect(page.getByText("3 total · page 1 of 1")).toBeVisible();
  expect(api.requests[0]?.searchParams.get("pageSize")).toBe("25");

  await page.getByLabel("Severity").selectOption("Critical");
  await expect(list.getByRole("button")).toHaveCount(1);
  await expect(list.getByRole("button")).toContainText("OpenSSL buffer overflow");
  expect(api.requests.at(-1)?.searchParams.get("severity")).toBe("Critical");

  await page.getByLabel("Severity").selectOption("");
  await page.getByLabel("Scanner").selectOption("Gitleaks");
  await expect(list.getByRole("button")).toHaveCount(1);
  await expect(list.getByRole("button")).toContainText("Generic API key");

  await page.getByLabel("Scanner").selectOption("");
  await page.getByLabel("Status").selectOption("Suppressed");
  await expect(list.getByRole("button")).toHaveCount(1);
  await expect(list.getByRole("button")).toContainText("Suppressed");

  await page.getByLabel("Status").selectOption("Open");
  await page.getByLabel("Artifact").selectOption(artifactNew);
  await expect(list.getByRole("button")).toHaveCount(1);
  await expect(list.getByRole("button")).toContainText("SQL injection");
  const last = api.requests.at(-1)?.searchParams;
  expect([last?.get("status"), last?.get("artifactId"), last?.get("page")]).toEqual(["Open", artifactNew, "1"]);

  await page.getByLabel("Severity").selectOption("Low");
  await expect(list.getByText("No findings match these filters.")).toBeVisible();
});

test("opening a finding shows the dedicated detail view", async ({ page }) => {
  await mockFindingsApi(page, ["findings:read"]);
  await page.getByRole("button", { name: /OpenSSL buffer overflow/ }).click();

  const detail = page.locator(".finding-detail");
  await expect(detail.getByRole("heading", { name: "OpenSSL buffer overflow" })).toBeVisible();
  await expect(detail).toContainText("CVE-2024-0001");
  await expect(detail).toContainText("A crafted certificate can overflow a buffer.");
  await expect(detail).toContainText("Dockerfile:12");
  await expect(detail).toContainText("openssl 3.0.1 → fixed in 3.0.9");
  await expect(detail).toContainText("fp-openssl");
  await expect(detail).toContainText("Not suppressed");
  const help = detail.getByRole("link", { name: "https://avd.aquasec.com/nvd/cve-2024-0001" });
  await expect(help).toHaveAttribute("rel", "noopener noreferrer");
  await expect(help).toHaveAttribute("target", "_blank");

  await detail.getByRole("button", { name: "Back to findings" }).click();
  await expect(page.locator(".finding-detail")).toHaveCount(0);
});

test("a user with findings:suppress suppresses a finding with a reason and sees API errors", async ({ page }) => {
  const api = await mockFindingsApi(page, ["findings:read", "findings:suppress"]);
  await page.getByRole("button", { name: /SQL injection/ }).click();
  const form = page.getByRole("form", { name: "Suppress finding" });
  const suppress = form.getByRole("button", { name: "Suppress" });
  await expect(suppress).toBeDisabled();

  api.rejectNext();
  await form.getByLabel("Reason (required)").fill("False positive: parameterized by the ORM");
  await form.getByLabel("Expires (optional)").fill("2030-01-15T09:30");
  await suppress.click();
  await expect(form.getByRole("alert")).toHaveText("Suppression expiry must be in the future.");

  await suppress.click();
  await expect(page.getByRole("status").filter({ hasText: "Suppressed csharp.sqli" })).toBeVisible();
  expect(api.suppressions).toHaveLength(1);
  const sent = api.suppressions[0] as { fingerprint: string; reason: string; expiresAt: string };
  expect(sent.fingerprint).toBe("fp-sqli");
  expect(sent.reason).toBe("False positive: parameterized by the ORM");
  expect(new Date(sent.expiresAt).toISOString()).toBe(sent.expiresAt);
  expect(sent.expiresAt.startsWith("2030-01-1")).toBe(true);

  const detail = page.locator(".finding-detail");
  await expect(detail.getByTestId("suppression-info")).toContainText("False positive: parameterized by the ORM");
  await expect(detail.locator(".finding-status")).toHaveText("Suppressed");
});

test("the suppress action is hidden without findings:suppress", async ({ page }) => {
  await mockFindingsApi(page, ["findings:read"]);
  await page.getByRole("button", { name: /Generic API key/ }).click();

  const detail = page.locator(".finding-detail");
  await expect(detail.getByTestId("suppression-info")).toContainText("Test fixture key");
  await expect(detail.getByRole("button", { name: "Suppress" })).toHaveCount(0);
  await expect(page.getByRole("form", { name: "Suppress finding" })).toHaveCount(0);
});
