import { expect, test, type Page } from "@playwright/test";
import { signInWithPermissions } from "./mockSession";

// Typed policy rule forms (docs/05 §4, roadmap v1.2 item 5). The API is mocked; every save asserts the exact JSON
// the policy engine reads.

type Rule = { id: string; type: string; effect: string; order: number; isEnabled: boolean; parameters: object };
type Policy = { id: string; name: string; version: number; rules: Rule[] } & Record<string, unknown>;

const allRules: Rule[] = [
  { type: "RequireTestsPassed", effect: "Deny", parameters: {} },
  { type: "MaxFindings", effect: "Deny", parameters: { severity: "Critical", max: 0 } },
  { type: "RequireApprovals", effect: "RequireApproval", parameters: { count: 2, roles: ["Approver", "Security"] } },
  { type: "AllowedBranches", effect: "Deny", parameters: { patterns: ["main", "refs/tags/v*"] } },
  {
    type: "DeploymentWindow",
    effect: "Deny",
    parameters: {
      timezone: "Asia/Kuala_Lumpur",
      allowed: [
        { days: ["Mon", "Tue", "Wed", "Thu"], from: "09:00", to: "18:00" },
        { days: ["Fri"], from: "09:00", to: "15:00" },
      ],
    },
  },
  { type: "RequireImageDigest", effect: "Deny", parameters: {} },
  { type: "RequirePriorEnvironment", effect: "Deny", parameters: { tier: "Staging", maxAgeHours: 720 } },
  { type: "RequireScan", effect: "Deny", parameters: { scanners: ["Gitleaks", "Trivy", "Semgrep"], maxAgeHours: 168 } },
].map((rule, order) => ({ ...rule, id: `rule-${order}`, order, isEnabled: true }));

type Problem = { status: number; body: Record<string, unknown> };

async function mockPolicies(page: Page, rules: Rule[], options: { fail?: Problem } = {}) {
  let policy: Policy = {
    id: "00000000-0000-7000-8000-000000000901",
    name: "Production gate",
    description: "Every rule type.",
    scope: "Global",
    scopeId: null,
    isEnabled: true,
    appliesToTiers: ["Production"],
    version: 1,
    rules,
  };
  const writes: Array<{ rules: Array<{ type: string; order: number; parameters: object }> }> = [];
  await page.route("**/api/v1/policies**", async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith("/rule-types")) {
      await route.fulfill({ json: [{ type: "RequireImageDigest", effect: "Deny", parameters: {} }] });
    } else if (route.request().method() === "PUT") {
      const body = route.request().postDataJSON() as (typeof writes)[number];
      writes.push(body);
      if (options.fail) {
        await route.fulfill({
          status: options.fail.status,
          contentType: "application/problem+json",
          body: JSON.stringify(options.fail.body),
        });
        return;
      }
      policy = {
        ...policy,
        version: policy.version + 1,
        rules: body.rules.map((rule, index) => ({ ...rule, id: `saved-${index}`, isEnabled: true })),
      } as Policy;
      await route.fulfill({ json: policy });
    } else {
      await route.fulfill({ json: [policy] });
    }
  });
  return writes;
}

async function openPolicies(page: Page) {
  await signInWithPermissions(page, ["policies:read", "policies:write"]);
  await page.getByRole("button", { name: "Policies", exact: true }).click();
  await expect(page.getByRole("heading", { name: /Production gate v1/ })).toBeVisible();
}

const ruleItem = (page: Page, name: string) => page.getByRole("listitem", { name, exact: true });

test("each typed form saves the exact parameter shape the engine reads", async ({ page }) => {
  const writes = await mockPolicies(page, allRules);
  await openPolicies(page);
  const saved = (type: string) => writes.at(-1)!.rules.find((rule) => rule.type === type)!.parameters;

  // Rules without parameters explain themselves and have no editor.
  const tests = ruleItem(page, "Require tests passed");
  await expect(tests).toContainText("Deny unless the artifact's build and tests both passed in CI.");
  await expect(tests.getByRole("button")).toHaveCount(0);
  await expect(ruleItem(page, "Require image digest")).toContainText("immutable image digest");

  const findings = ruleItem(page, "Max findings");
  await expect(findings).toContainText("Deny if there are more than 0 open Critical findings from any scanner.");
  await findings.getByLabel("Severity", { exact: true }).selectOption("High");
  await findings.getByLabel("Maximum allowed").fill("3");
  await findings.getByLabel("Scanner").selectOption("Trivy");
  await findings.getByLabel("Also count findings with Unknown severity").check();
  await expect(findings).toContainText(
    "Deny if there are more than 3 open High-or-higher findings from Trivy (counting Unknown severity).",
  );
  await findings.getByRole("button", { name: "Save Max findings rule 2" }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(saved("MaxFindings")).toEqual({ scanner: "Trivy", severity: "High", max: 3, includeUnknown: true });
  // The other rules are sent back unchanged.
  expect(saved("RequirePriorEnvironment")).toEqual({ tier: "Staging", maxAgeHours: 720 });
  expect(writes[0].rules.map((rule) => rule.order)).toEqual([0, 1, 2, 3, 4, 5, 6, 7]);

  const approvals = ruleItem(page, "Require approvals");
  await approvals.getByLabel("Approvals required").fill("3");
  await approvals.getByRole("checkbox", { name: "Security" }).uncheck();
  await approvals.getByRole("checkbox", { name: "Admin" }).check();
  await expect(approvals).toContainText("Require 3 approvals from users with the Approver or Admin role");
  await approvals.getByRole("button", { name: "Save Require approvals rule 3" }).click();
  await expect.poll(() => writes.length).toBe(2);
  expect(saved("RequireApprovals")).toEqual({ count: 3, roles: ["Approver", "Admin"] });

  const branches = ruleItem(page, "Allowed branches");
  await branches.getByRole("button", { name: "Add pattern" }).click();
  await branches.getByRole("textbox", { name: "Branch pattern 3" }).fill(" release/* ");
  await branches.getByRole("button", { name: "Remove branch pattern 1" }).click();
  await expect(branches.getByRole("textbox", { name: "Branch pattern 1" })).toHaveValue("refs/tags/v*");
  await branches.getByRole("button", { name: "Save Allowed branches rule 4" }).click();
  await expect.poll(() => writes.length).toBe(3);
  expect(saved("AllowedBranches")).toEqual({ patterns: ["refs/tags/v*", "release/*"] });

  const windowRule = ruleItem(page, "Deployment window");
  await expect(windowRule).toContainText("Deny outside Mon–Thu 09:00–18:00 and Fri 09:00–15:00 (Asia/Kuala_Lumpur).");
  await windowRule.getByLabel("Time zone").selectOption("UTC");
  const friday = windowRule.getByRole("group", { name: "Window 2", exact: true });
  await friday.getByRole("checkbox", { name: "Sat" }).check();
  await friday.getByLabel("Until").fill("16:30");
  await windowRule.getByRole("button", { name: "Add window" }).click();
  await windowRule.getByRole("button", { name: "Remove window 1" }).click();
  await expect(windowRule).toContainText("Deny outside Fri–Sat 09:00–16:30 and Mon–Fri 09:00–18:00 (UTC).");
  await windowRule.getByRole("button", { name: "Save Deployment window rule 5" }).click();
  await expect.poll(() => writes.length).toBe(4);
  expect(saved("DeploymentWindow")).toEqual({
    timezone: "UTC",
    allowed: [
      { days: ["Fri", "Sat"], from: "09:00", to: "16:30" },
      { days: ["Mon", "Tue", "Wed", "Thu", "Fri"], from: "09:00", to: "18:00" },
    ],
  });

  const prior = ruleItem(page, "Require prior environment");
  await prior.getByLabel("Must first succeed in").selectOption("Development");
  await prior.getByLabel("Within (hours)").fill("");
  await expect(prior).toContainText("Deny unless the same artifact was deployed successfully to Development.");
  await prior.getByRole("button", { name: "Save Require prior environment rule 7" }).click();
  await expect.poll(() => writes.length).toBe(5);
  expect(saved("RequirePriorEnvironment")).toEqual({ tier: "Development", status: "Succeeded" });

  const scan = ruleItem(page, "Require scan");
  await scan.getByRole("checkbox", { name: "Gitleaks" }).uncheck();
  await scan.getByLabel("Maximum scan age (hours)").fill("24");
  await expect(scan).toContainText(
    "Deny unless Trivy and Semgrep each have a completed scan in the last 24 hours (1 day).",
  );
  await scan.getByRole("button", { name: "Save Require scan rule 8" }).click();
  await expect.poll(() => writes.length).toBe(6);
  expect(saved("RequireScan")).toEqual({ scanners: ["Trivy", "Semgrep"], maxAgeHours: 24 });
});

test("save and cancel follow the dirty state and the form blocks out-of-range values", async ({ page }) => {
  const writes = await mockPolicies(page, allRules);
  await openPolicies(page);
  const approvals = ruleItem(page, "Require approvals");
  const save = approvals.getByRole("button", { name: "Save Require approvals rule 3" });
  const cancel = approvals.getByRole("button", { name: "Cancel Require approvals rule 3 changes" });
  await expect(save).toBeDisabled();
  await expect(cancel).toBeDisabled();

  await approvals.getByLabel("Approvals required").fill("11");
  await expect(approvals.getByText("Enter a whole number from 1 to 10.")).toBeVisible();
  await expect(approvals.getByLabel("Approvals required")).toHaveAttribute("aria-invalid", "true");
  await expect(save).toBeDisabled();
  await expect(approvals.getByText("Unsaved changes")).toBeVisible();
  await expect(cancel).toBeEnabled();

  // Changing a value back to the stored one is not a change.
  await approvals.getByLabel("Approvals required").fill("2");
  await expect(save).toBeDisabled();
  await approvals.getByLabel("Approvals required").fill("4");
  await expect(save).toBeEnabled();
  await cancel.click();
  await expect(approvals.getByLabel("Approvals required")).toHaveValue("2");
  await expect(save).toBeDisabled();

  const windowRule = ruleItem(page, "Deployment window");
  await windowRule.getByRole("group", { name: "Window 1", exact: true }).getByLabel("Until").fill("08:00");
  await expect(
    windowRule.getByText("The end must be after the start; split windows that cross midnight."),
  ).toBeVisible();
  await expect(windowRule.getByRole("button", { name: "Save Deployment window rule 5" })).toBeDisabled();
  expect(writes).toHaveLength(0);
});

test("parameters the form cannot represent open the JSON editor with a notice", async ({ page }) => {
  const legacy: Rule[] = [
    {
      id: "legacy",
      type: "RequireScan",
      effect: "Deny",
      order: 0,
      isEnabled: true,
      parameters: { scanners: ["Gitleaks", "Trivy"], maxAgeDays: 7 },
    },
    allRules[1],
  ].map((rule, order) => ({ ...rule, order }));
  const writes = await mockPolicies(page, legacy);
  await openPolicies(page);

  const scan = ruleItem(page, "Require scan");
  await expect(scan.getByRole("note")).toContainText('"maxAgeDays" is not a RequireScan field.');
  const json = scan.getByLabel("Require scan rule 1 parameters JSON");
  await expect(json).toHaveValue(/"maxAgeDays": 7/);
  await expect(scan.getByLabel("Maximum scan age (hours)")).toHaveCount(0);
  await json.fill('{ "scanners": ["Gitleaks", "Trivy"], "maxAgeHours": 168 }');
  await scan.getByRole("button", { name: "Save Require scan rule 1" }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].rules[0].parameters).toEqual({ scanners: ["Gitleaks", "Trivy"], maxAgeHours: 168 });
  // Once the stored shape fits, the typed form is back.
  await expect(scan.getByLabel("Maximum scan age (hours)")).toHaveValue("168");

  // Advanced JSON on a typed rule round-trips into the form.
  const findings = ruleItem(page, "Max findings");
  await findings.getByRole("button", { name: "Advanced: edit JSON" }).click();
  const findingsJson = findings.getByLabel("Max findings rule 2 parameters JSON");
  await expect(findingsJson).toHaveValue(/"includeUnknown": false/);
  await findingsJson.fill('{ "scanner": null, "severity": "Medium", "max": 5, "includeUnknown": false }');
  await findings.getByRole("button", { name: "Back to form" }).click();
  await expect(findings.getByLabel("Maximum allowed")).toHaveValue("5");
  await expect(findings.getByLabel("Severity", { exact: true })).toHaveValue("Medium");

  // JSON the form cannot show keeps the editor open with an explanation.
  await findings.getByRole("button", { name: "Advanced: edit JSON" }).click();
  await findingsJson.fill('{ "severity": "Medium", "threshold": 5 }');
  await findings.getByRole("button", { name: "Back to form" }).click();
  await expect(findings.getByRole("alert")).toContainText('"threshold" is not a MaxFindings field.');
  await expect(findingsJson).toBeVisible();
});

test("server validation errors are shown on the rule and keep the draft", async ({ page }) => {
  const writes = await mockPolicies(page, allRules, {
    fail: {
      status: 400,
      body: {
        title: 'DeploymentWindow rule 5: "timezone" must be an IANA time zone such as Asia/Kuala_Lumpur or UTC.',
        status: 400,
        ruleType: "DeploymentWindow",
        ruleOrder: 4,
        field: "timezone",
      },
    },
  });
  await openPolicies(page);
  const windowRule = ruleItem(page, "Deployment window");
  await windowRule.getByLabel("Time zone").selectOption("Europe/London");
  await windowRule.getByRole("button", { name: "Save Deployment window rule 5" }).click();
  await expect.poll(() => writes.length).toBe(1);
  await expect(windowRule.getByRole("alert")).toContainText(
    'DeploymentWindow rule 5: "timezone" must be an IANA time zone',
  );
  await expect(windowRule.getByLabel("Time zone")).toHaveValue("Europe/London");
  await expect(windowRule.getByRole("button", { name: "Save Deployment window rule 5" })).toBeEnabled();
  await expect(page.getByRole("heading", { name: /Production gate v1/ })).toBeVisible();
});
