import { expect, test, type Page } from "@playwright/test";
import { mockSession, signIn } from "./mockSession";
import { currentUser, mockWorkspace, permissions } from "./workspaceFixtures";

// Visits every screen with realistic mocked data in the light and the dark theme at 1440×900 and checks that the
// theme is applied and nothing overflows horizontally. With VISUAL_TOUR_DIR set, it also saves full-page screenshots
// there as <theme>-<nn>-<page>.png for a visual review. No real backend is involved.
// No @types/node in this package: read the environment through a typed view of globalThis.process.
const env = (globalThis as { process?: { env: Record<string, string | undefined> } }).process?.env ?? {};
const screenshotDir = env.VISUAL_TOUR_DIR?.replace(/\/+$/, "");

test.use({ viewport: { width: 1440, height: 900 } });

async function check(page: Page, theme: "light" | "dark", index: number, name: string) {
  await expect(page.locator("html")).toHaveAttribute("data-theme", theme);
  // Let pending fetches settle so the screenshot shows the loaded state.
  await page.waitForLoadState("networkidle");
  const overflow = await page.evaluate(() => {
    const scroller = document.scrollingElement ?? document.documentElement;
    return { scrollWidth: scroller.scrollWidth, innerWidth: window.innerWidth };
  });
  expect(overflow.scrollWidth, `${name} overflows horizontally`).toBeLessThanOrEqual(overflow.innerWidth);
  if (screenshotDir) {
    await page.screenshot({
      path: `${screenshotDir}/${theme}-${String(index).padStart(2, "0")}-${name}.png`,
      fullPage: true,
      animations: "disabled",
    });
  }
}

async function open(page: Page, label: string) {
  await page
    .getByRole("navigation", { name: "Main navigation" })
    .getByRole("button", { name: label, exact: true })
    .click();
  await expect(page.getByRole("heading", { level: 1, name: label })).toBeVisible();
}

for (const theme of ["light", "dark"] as const) {
  test(`visual tour in the ${theme} theme`, async ({ page }) => {
    test.setTimeout(60_000);
    await page.addInitScript((value) => window.localStorage.setItem("aegisops.theme", value), theme);
    await mockSession(page, permissions, currentUser);
    await mockWorkspace(page);

    await page.goto("/");
    await expect(page.getByRole("heading", { name: "Sign in to AegisOps" })).toBeVisible();
    await check(page, theme, 0, "sign-in");

    await signIn(page);
    await expect(page.getByRole("heading", { level: 1, name: "Overview" })).toBeVisible();
    await expect(page.locator(".stat-card").first()).toBeVisible();
    await check(page, theme, 1, "overview");

    await open(page, "Projects");
    await page.getByRole("button", { name: /Circuit service/ }).click();
    await expect(page.getByRole("heading", { name: /circuit-service/ })).toBeVisible();
    await expect(page.getByRole("list", { name: "Staging variable list" })).toBeVisible();
    await check(page, theme, 2, "projects");

    await open(page, "Deployments");
    await page.getByRole("button", { name: /Awaiting approval/ }).click();
    await expect(page.locator("ol.timeline > li")).toHaveCount(4);
    await check(page, theme, 3, "deployments");

    await open(page, "Approvals");
    await expect(page.getByRole("button", { name: "Approve" })).toHaveCount(2);
    await check(page, theme, 4, "approvals");

    await open(page, "Findings");
    await expect(page.getByRole("list", { name: "Findings list" }).getByRole("button")).toHaveCount(7);
    await check(page, theme, 5, "findings");
    await page.getByRole("button", { name: /regreSSHion/ }).click();
    await expect(page.locator(".finding-detail")).toBeVisible();
    await check(page, theme, 6, "finding-detail");

    await open(page, "Policies");
    await expect(page.getByRole("heading", { name: /Production baseline v4/ })).toBeVisible();
    await check(page, theme, 7, "policies");

    await open(page, "AI Governance");
    await expect(page.getByRole("cell", { name: "SensitiveData", exact: true })).toBeVisible();
    await check(page, theme, 8, "ai-governance");

    await open(page, "AI Playground");
    await page.getByLabel("Prompt").fill("Summarize the latest Production deployment for my reviewer.");
    await page.getByRole("button", { name: "Send" }).click();
    await expect(page.getByText("AI-generated — verify before acting.")).toBeVisible();
    await check(page, theme, 9, "ai-playground");

    await open(page, "Audit log");
    await expect(page.getByRole("cell", { name: "deployment.requested" })).toBeVisible();
    await check(page, theme, 10, "audit");

    await open(page, "Administration");
    await expect(page.getByRole("listitem", { name: "dev@aegisops.local" })).toBeVisible();
    await check(page, theme, 11, "admin-users");
    await page.getByRole("tab", { name: /Teams/ }).click();
    await expect(page.getByRole("heading", { name: /Platform/ })).toBeVisible();
    await check(page, theme, 12, "admin-teams");
    await page.getByRole("tab", { name: /Jobs/ }).click();
    await expect(page.getByRole("button", { name: "Retry" })).toHaveCount(2);
    await check(page, theme, 13, "admin-jobs");
  });
}

test("narrow windows collapse the sidebar without horizontal scrolling", async ({ page }) => {
  await page.setViewportSize({ width: 768, height: 900 });
  await mockSession(page, permissions, currentUser);
  await mockWorkspace(page);
  await signIn(page);
  for (const label of ["Overview", "Projects", "Deployments", "Findings", "AI Governance", "Administration"]) {
    await page
      .getByRole("navigation", { name: "Main navigation" })
      .getByRole("button", { name: label, exact: true })
      .click();
    await expect(page.getByRole("heading", { level: 1, name: label })).toBeVisible();
    if (label === "Projects") await page.getByRole("button", { name: /Circuit service/ }).click();
    await page.waitForLoadState("networkidle");
    const width = await page.evaluate(() => (document.scrollingElement ?? document.documentElement).scrollWidth);
    expect(width, `${label} overflows at 768px`).toBeLessThanOrEqual(768);
    if (screenshotDir) {
      await page.screenshot({
        path: `${screenshotDir}/narrow-${label.toLowerCase().replace(/\s+/g, "-")}.png`,
        fullPage: true,
      });
    }
  }
});
