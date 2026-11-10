import { expect, test, type Page } from "@playwright/test";
import { signInWithPermissions } from "./mockSession";

// Mocked API routes only (no live backend or Ollama): shapes follow src/AegisOps.Api/Ai/AiEndpoints.cs.
const allowedModels = [
  { id: "00000000-0000-7000-8000-00000000a001", name: "llama3.2:3b", displayName: "Llama 3.2 3B", provider: "Ollama" },
  { id: "00000000-0000-7000-8000-00000000a002", name: "qwen2.5-coder:7b", displayName: null, provider: "Ollama" },
];

async function openPlayground(page: Page) {
  await page.route("**/api/v1/ai/models", (route) => route.fulfill({ json: allowedModels }));
  await signInWithPermissions(page, ["ai:chat", "findings:read"]);
  await page.getByRole("button", { name: "AI Playground", exact: true }).click();
  await expect(page.getByRole("heading", { name: "AI Playground" })).toBeVisible();
}

function problem(status: number, reason: string) {
  return {
    status,
    contentType: "application/problem+json",
    body: JSON.stringify({ title: reason, status, reason, requestId: "00000000-0000-7000-8000-00000000b001" }),
  };
}

test("model selector lists only the models the caller's AI policy allows", async ({ page }) => {
  await openPlayground(page);

  const select = page.getByLabel("Model");
  await expect(select.locator("option")).toHaveText(["Llama 3.2 3B · Ollama", "qwen2.5-coder:7b · Ollama"]);
  await expect(select).toHaveValue("llama3.2:3b");
  await expect(page.getByRole("note")).toContainText("may be logged as redacted previews");
});

test("AI Playground is hidden without the ai:chat permission", async ({ page }) => {
  await signInWithPermissions(page, ["findings:read"]);
  await expect(page.getByRole("button", { name: "AI Governance", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "AI Playground", exact: true })).toHaveCount(0);
});

test("a successful reply shows the conversation and the governance panel with redaction", async ({ page }) => {
  let sent: { model?: string; messages?: Array<{ role: string; content: string }> } | null = null;
  await page.route("**/api/v1/ai/chat", async (route) => {
    sent = route.request().postDataJSON() as typeof sent;
    await new Promise((resolve) => setTimeout(resolve, 300));
    await route.fulfill({
      json: {
        id: "00000000-0000-7000-8000-00000000c001",
        model: "qwen2.5-coder:7b",
        message: { role: "assistant", content: "The key [REDACTED:AWS_ACCESS_KEY] was removed before forwarding." },
        usage: { promptTokens: 21, completionTokens: 12, latencyMs: 420 },
        governance: {
          policy: "00000000-0000-7000-8000-00000000d001",
          sensitiveData: { action: "Redact", detected: ["AWS_ACCESS_KEY"], redactions: 1 },
          rateLimit: { remainingMinute: 12, remainingDay: 488 },
        },
      },
    });
  });
  await openPlayground(page);

  const prompt = "Explain this error: AKIAIOSFODNN7EXAMPLE";
  await page.getByLabel("Model").selectOption("qwen2.5-coder:7b");
  await page.getByLabel("Prompt").fill(prompt);
  await page.getByRole("button", { name: "Send" }).click();
  await expect(page.getByRole("button", { name: "Sending…" })).toBeDisabled();

  const history = page.getByRole("list", { name: "Conversation" });
  await expect(history.getByText(prompt)).toBeVisible();
  await expect(history.getByText("[REDACTED:AWS_ACCESS_KEY] was removed")).toBeVisible();
  await expect(page.getByLabel("Prompt")).toHaveValue("");
  expect(sent).toEqual({ model: "qwen2.5-coder:7b", messages: [{ role: "user", content: prompt }] });

  await expect(page.getByTestId("governance-action")).toHaveText("Redact");
  await expect(page.getByTestId("governance-detected")).toHaveText("AWS_ACCESS_KEY");
  await expect(page.getByTestId("governance-redactions")).toHaveText("1");
  await expect(page.getByTestId("governance-block-reason")).toHaveText("None");
  await expect(page.getByTestId("governance-rate-limit")).toHaveText("12 this minute · 488 today");

  // Prompts never reach browser storage.
  const stored = await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }));
  expect(stored).not.toContain("AKIAIOSFODNN7EXAMPLE");
});

test("a sensitive-data block (422) keeps the draft and reports the block reason", async ({ page }) => {
  await page.route("**/api/v1/ai/chat", (route) => route.fulfill(problem(422, "SensitiveData")));
  await openPlayground(page);

  const prompt = "My NRIC is 900101-14-5678";
  await page.getByLabel("Prompt").fill(prompt);
  await page.getByRole("button", { name: "Send" }).click();

  await expect(
    page.getByRole("alert").filter({ hasText: "Blocked: the prompt contains sensitive data" }),
  ).toBeVisible();
  await expect(page.getByLabel("Prompt")).toHaveValue(prompt);
  await expect(page.getByRole("list", { name: "Conversation" }).getByText(prompt)).toHaveCount(0);
  await expect(page.getByTestId("governance-block-reason")).toHaveText("SensitiveData");
  await expect(page.getByTestId("governance-action")).toHaveText("Block");
  await expect(page.locator(".governance-panel .ai-status")).toHaveText("Blocked");
});

test("a rate-limited request (429) shows the limit was reached", async ({ page }) => {
  await page.route("**/api/v1/ai/chat", (route) => route.fulfill(problem(429, "RateLimited")));
  await openPlayground(page);

  await page.getByLabel("Prompt").fill("Summarize the last deployment");
  await page.getByRole("button", { name: "Send" }).click();

  await expect(page.getByRole("alert").filter({ hasText: "Rate limit reached" })).toBeVisible();
  await expect(page.getByTestId("governance-rate-limit")).toHaveText("Limit reached");
  await expect(page.getByTestId("governance-block-reason")).toHaveText("RateLimited");
  await expect(page.getByRole("button", { name: "Send" })).toBeEnabled();
});
