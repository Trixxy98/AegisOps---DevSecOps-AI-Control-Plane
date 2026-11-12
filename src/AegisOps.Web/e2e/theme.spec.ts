import { expect, test } from "@playwright/test";
import { signInWithPermissions } from "./mockSession";

// The theme control in the sidebar footer: System (default, follows prefers-color-scheme), Light and Dark.
// Only the preference is stored, under one localStorage key.
const storedTheme = (page: import("@playwright/test").Page) =>
  page.evaluate(() => window.localStorage.getItem("aegisops.theme"));

test("the theme control switches the theme and the choice survives a reload", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "light" });
  await signInWithPermissions(page, []);
  const html = page.locator("html");
  const theme = page.getByRole("group", { name: "Theme" });

  await expect(html).toHaveAttribute("data-theme", "light");
  await expect(theme.getByRole("button", { name: "System theme" })).toHaveAttribute("aria-pressed", "true");
  expect(await storedTheme(page)).toBeNull();

  await theme.getByRole("button", { name: "Dark theme" }).click();
  await expect(html).toHaveAttribute("data-theme", "dark");
  await expect(theme.getByRole("button", { name: "Dark theme" })).toHaveAttribute("aria-pressed", "true");
  expect(await storedTheme(page)).toBe("dark");

  // The access token lives in memory, so a reload returns to sign-in, already in the stored theme.
  await page.reload();
  await expect(page.getByRole("button", { name: "Continue" })).toBeVisible();
  await expect(html).toHaveAttribute("data-theme", "dark");
  expect(await page.evaluate(() => document.documentElement.style.colorScheme)).toBe("dark");

  await page.getByLabel("Password").fill("not-a-real-password");
  await page.getByRole("button", { name: "Continue" }).click();
  await theme.getByRole("button", { name: "Light theme" }).click();
  await expect(html).toHaveAttribute("data-theme", "light");
  expect(await storedTheme(page)).toBe("light");

  // Back to System: the stored choice is removed and the page follows the OS setting live.
  await theme.getByRole("button", { name: "System theme" }).click();
  expect(await storedTheme(page)).toBeNull();
  await page.emulateMedia({ colorScheme: "dark" });
  await expect(html).toHaveAttribute("data-theme", "dark");
  await page.emulateMedia({ colorScheme: "light" });
  await expect(html).toHaveAttribute("data-theme", "light");
});

test("without a stored choice the first paint follows the system preference", async ({ page }) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await page.goto("/");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await expect(page.getByRole("heading", { name: "Sign in to AegisOps" })).toBeVisible();
});
