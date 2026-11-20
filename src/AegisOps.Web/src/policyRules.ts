// Typed models for deployment policy rule parameters (docs/05 §4). Each model mirrors the parameter record the
// policy engine reads (src/AegisOps.Application/PolicyEngine/PolicyEngine.cs) and the save-time validation in
// PolicyRuleParameters.cs; `serializeRule` produces exactly the JSON the engine expects.

export const SCANNERS = ["Gitleaks", "Trivy", "Semgrep"] as const;
export const SEVERITIES = ["Critical", "High", "Medium", "Low", "Info"] as const;
export const APPROVER_ROLES = ["Approver", "Security", "Admin"] as const;
export const TIERS = ["Development", "Staging", "Production"] as const;
export const DAYS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"] as const;
export const DEFAULT_TIMEZONE = "Asia/Kuala_Lumpur";
export const LIMITS = {
  approvals: [1, 10],
  findings: [0, 10000],
  ageHours: [1, 8760],
  windows: 50,
  patterns: 50,
} as const;

export type WindowSlot = { days: string[]; from: string; to: string };

/** Number fields keep the text the user typed; "" means "not set" where the engine has a default or no limit. */
export type RuleModel =
  | { type: "RequireTestsPassed" }
  | { type: "RequireImageDigest" }
  | { type: "RequireScan"; scanners: string[]; maxAgeHours: string }
  | { type: "MaxFindings"; scanner: string; severity: string; max: string; includeUnknown: boolean }
  | { type: "RequireApprovals"; count: string; roles: string[] }
  | { type: "AllowedBranches"; patterns: string[] }
  | { type: "DeploymentWindow"; timezone: string; allowed: WindowSlot[] }
  | { type: "RequirePriorEnvironment"; tier: string; maxAgeHours: string };

export type ParseResult = { ok: true; model: RuleModel } | { ok: false; reason: string };

const FIELDS: Record<RuleModel["type"], string[]> = {
  RequireTestsPassed: [],
  RequireImageDigest: [],
  RequireScan: ["scanners", "maxAgeHours"],
  MaxFindings: ["scanner", "severity", "max", "includeUnknown"],
  RequireApprovals: ["count", "roles"],
  AllowedBranches: ["patterns"],
  DeploymentWindow: ["timezone", "allowed"],
  RequirePriorEnvironment: ["tier", "status", "maxAgeHours"],
};

export function isKnownRuleType(type: string): type is RuleModel["type"] {
  return Object.prototype.hasOwnProperty.call(FIELDS, type);
}

export function hasParameters(type: string) {
  return isKnownRuleType(type) && FIELDS[type].length > 0;
}

class NotClean extends Error {}

function fail(reason: string): never {
  throw new NotClean(reason);
}

/** Case-insensitive match against a fixed list, returning the canonical spelling. */
function pick(value: unknown, options: readonly string[], field: string): string {
  const match =
    typeof value === "string" ? options.find((option) => option.toLowerCase() === value.toLowerCase()) : undefined;
  return match ?? fail(`"${field}" has an unexpected value ${JSON.stringify(value)}.`);
}

function whole(value: unknown, field: string, fallback: string): string {
  if (value === undefined || value === null) return fallback;
  return typeof value === "number" && Number.isInteger(value)
    ? String(value)
    : fail(`"${field}" is not a whole number.`);
}

function list(value: unknown, field: string): unknown[] {
  if (value === undefined || value === null) return [];
  return Array.isArray(value) ? value : fail(`"${field}" is not a list.`);
}

const CLOCK = /^(\d{1,2}):(\d{2})(?::(\d{2})(?:\.(\d{1,7}))?)?$/;

/**
 * Parses stored parameters into the typed model. Anything the form cannot represent without losing or changing
 * meaning (unknown fields, wrong types, values outside the known sets) is reported so the UI opens the JSON editor.
 */
export function parseRule(type: string, parameters: unknown): ParseResult {
  if (!isKnownRuleType(type)) return { ok: false, reason: `There is no form for the ${type} rule type.` };
  if (parameters === null || typeof parameters !== "object" || Array.isArray(parameters)) {
    return { ok: false, reason: "The stored parameters are not a JSON object." };
  }
  const values = parameters as Record<string, unknown>;
  try {
    const unknown = Object.keys(values).find((key) => !FIELDS[type].includes(key));
    if (unknown !== undefined) fail(`"${unknown}" is not a ${type} field.`);
    switch (type) {
      case "RequireTestsPassed":
      case "RequireImageDigest":
        return { ok: true, model: { type } };
      case "RequireScan": {
        const scanners = list(values.scanners, "scanners").map((item) => pick(item, SCANNERS, "scanners"));
        return { ok: true, model: { type, scanners, maxAgeHours: whole(values.maxAgeHours, "maxAgeHours", "") } };
      }
      case "MaxFindings": {
        const scanner =
          values.scanner === undefined || values.scanner === null ? "" : pick(values.scanner, SCANNERS, "scanner");
        const severity =
          values.severity === undefined || values.severity === null
            ? "Critical"
            : pick(values.severity, SEVERITIES, "severity");
        const includeUnknown = values.includeUnknown ?? false;
        if (typeof includeUnknown !== "boolean") fail(`"includeUnknown" is not true or false.`);
        return { ok: true, model: { type, scanner, severity, max: whole(values.max, "max", "0"), includeUnknown } };
      }
      case "RequireApprovals": {
        // Approval role names are compared exactly by the API, so no case folding here.
        const roles = list(values.roles, "roles").map((role) =>
          typeof role === "string" && (APPROVER_ROLES as readonly string[]).includes(role)
            ? role
            : fail(`"roles" has an unexpected value ${JSON.stringify(role)}.`),
        );
        return { ok: true, model: { type, count: whole(values.count, "count", "1"), roles } };
      }
      case "AllowedBranches": {
        const patterns = list(values.patterns, "patterns").map((item) =>
          typeof item === "string" ? item : fail(`"patterns" contains a value that is not text.`),
        );
        return { ok: true, model: { type, patterns } };
      }
      case "DeploymentWindow": {
        const timezone =
          values.timezone === undefined || values.timezone === null
            ? DEFAULT_TIMEZONE
            : typeof values.timezone === "string"
              ? values.timezone
              : fail(`"timezone" is not text.`);
        const allowed = list(values.allowed, "allowed").map((slot, index) => {
          if (slot === null || typeof slot !== "object" || Array.isArray(slot))
            fail(`Window ${index + 1} is not an object.`);
          const fields = slot as Record<string, unknown>;
          const extra = Object.keys(fields).find((key) => !["days", "from", "to"].includes(key));
          if (extra !== undefined) fail(`Window ${index + 1} has an unexpected field "${extra}".`);
          const days = list(fields.days, "days").map((day) => pick(day, DAYS, "days"));
          const from =
            typeof fields.from === "string" && CLOCK.test(fields.from)
              ? fields.from
              : fail(`Window ${index + 1} has no valid start time.`);
          const to =
            typeof fields.to === "string" && CLOCK.test(fields.to)
              ? fields.to
              : fail(`Window ${index + 1} has no valid end time.`);
          return { days, from, to };
        });
        return { ok: true, model: { type, timezone, allowed } };
      }
      case "RequirePriorEnvironment": {
        const tier = pick(values.tier, TIERS, "tier");
        if (values.status !== undefined && values.status !== null && values.status !== "Succeeded") {
          fail(`"status" has an unexpected value ${JSON.stringify(values.status)}.`);
        }
        return { ok: true, model: { type, tier, maxAgeHours: whole(values.maxAgeHours, "maxAgeHours", "") } };
      }
    }
  } catch (error) {
    if (error instanceof NotClean) return { ok: false, reason: error.message };
    throw error;
  }
}

function toNumber(value: string): number | null {
  const trimmed = value.trim();
  return trimmed === "" ? null : Number(trimmed);
}

function ordered(values: string[], order: readonly string[]) {
  return order.filter((item) => values.includes(item));
}

/** The JSON object the engine reads, in the documented shape (docs/05 §4). */
export function serializeRule(model: RuleModel): Record<string, unknown> {
  switch (model.type) {
    case "RequireTestsPassed":
    case "RequireImageDigest":
      return {};
    case "RequireScan": {
      const age = toNumber(model.maxAgeHours);
      return { scanners: ordered(model.scanners, SCANNERS), ...(age === null ? {} : { maxAgeHours: age }) };
    }
    case "MaxFindings":
      return {
        scanner: model.scanner === "" ? null : model.scanner,
        severity: model.severity,
        max: toNumber(model.max),
        includeUnknown: model.includeUnknown,
      };
    case "RequireApprovals":
      return {
        count: toNumber(model.count),
        ...(model.roles.length === 0 ? {} : { roles: ordered(model.roles, APPROVER_ROLES) }),
      };
    case "AllowedBranches":
      return { patterns: model.patterns.map((pattern) => pattern.trim()) };
    case "DeploymentWindow":
      return {
        timezone: model.timezone,
        allowed: model.allowed.map((slot) => ({ days: ordered(slot.days, DAYS), from: slot.from, to: slot.to })),
      };
    case "RequirePriorEnvironment": {
      const age = toNumber(model.maxAgeHours);
      return { tier: model.tier, status: "Succeeded", ...(age === null ? {} : { maxAgeHours: age }) };
    }
  }
}

/** Defaults for a new window row and for the form when a rule is added. */
export const NEW_WINDOW: WindowSlot = { days: ["Mon", "Tue", "Wed", "Thu", "Fri"], from: "09:00", to: "18:00" };

function inRange(value: string, [min, max]: readonly [number, number], optional: boolean) {
  const trimmed = value.trim();
  if (trimmed === "") return optional;
  const number = Number(trimmed);
  return Number.isInteger(number) && number >= min && number <= max;
}

export function minutes(clock: string): number {
  const match = CLOCK.exec(clock);
  if (!match) return Number.NaN;
  return Number(match[1]) * 60 + Number(match[2]) + Number(match[3] ?? 0) / 60 + Number(`0.${match[4] ?? 0}`) / 60;
}

/** Field-level problems the form can see before saving. The API validates again and is authoritative. */
export function formProblems(model: RuleModel): Record<string, string> {
  const problems: Record<string, string> = {};
  switch (model.type) {
    case "RequireScan":
      if (model.scanners.length === 0) problems.scanners = "Choose at least one scanner.";
      if (!inRange(model.maxAgeHours, LIMITS.ageHours, true))
        problems.maxAgeHours = `Enter 1 to ${LIMITS.ageHours[1]} hours, or leave empty for no age limit.`;
      break;
    case "MaxFindings":
      if (!inRange(model.max, LIMITS.findings, false))
        problems.max = `Enter a whole number from 0 to ${LIMITS.findings[1]}.`;
      break;
    case "RequireApprovals":
      if (!inRange(model.count, LIMITS.approvals, false)) problems.count = "Enter a whole number from 1 to 10.";
      break;
    case "AllowedBranches":
      if (model.patterns.length === 0) problems.patterns = "Add at least one branch pattern.";
      else if (model.patterns.length > LIMITS.patterns)
        problems.patterns = `Use at most ${LIMITS.patterns} branch patterns.`;
      model.patterns.forEach((pattern, index) => {
        if (pattern.trim() === "") problems[`patterns.${index}`] = "Enter a branch name or glob, or remove the row.";
      });
      break;
    case "DeploymentWindow":
      if (model.allowed.length === 0) problems.allowed = "Add at least one window.";
      model.allowed.forEach((slot, index) => {
        if (slot.days.length === 0) problems[`allowed.${index}.days`] = "Choose at least one day.";
        const from = minutes(slot.from);
        const to = minutes(slot.to);
        if (Number.isNaN(from) || Number.isNaN(to)) problems[`allowed.${index}.time`] = "Enter a start and end time.";
        else if (to <= from)
          problems[`allowed.${index}.time`] = "The end must be after the start; split windows that cross midnight.";
      });
      break;
    case "RequirePriorEnvironment":
      if (!inRange(model.maxAgeHours, LIMITS.ageHours, true))
        problems.maxAgeHours = `Enter 1 to ${LIMITS.ageHours[1]} hours, or leave empty for no age limit.`;
      break;
  }
  return problems;
}

/** Stable JSON for dirty checks (key order does not matter). */
export function canonical(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonical).join(",")}]`;
  if (value !== null && typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
    return `{${entries.map(([key, item]) => `${JSON.stringify(key)}:${canonical(item)}`).join(",")}}`;
  }
  return JSON.stringify(value) ?? "null";
}

// --- Plain-language explanations ----------------------------------------------------------------------------------

function joinOr(items: string[], word = "or") {
  if (items.length <= 1) return items.join("");
  return `${items.slice(0, -1).join(", ")} ${word} ${items[items.length - 1]}`;
}

function hours(value: string) {
  const number = Number(value);
  if (number % 24 === 0) {
    const days = number / 24;
    return `${number} hours (${days} ${days === 1 ? "day" : "days"})`;
  }
  return `${number} ${number === 1 ? "hour" : "hours"}`;
}

/** "HH:mm" for display; a stored end such as 23:59:59.9999999 reads as 24:00. */
export function clockLabel(clock: string) {
  const match = CLOCK.exec(clock);
  if (!match) return clock;
  if (match[1] === "23" && match[2] === "59" && match[3] === "59") return "24:00";
  return `${match[1].padStart(2, "0")}:${match[2]}`;
}

/** Collapses ordered days into ranges: Mon, Tue, Wed, Thu → Mon–Thu. */
export function dayRanges(days: string[]) {
  const indexes = ordered(days, DAYS).map((day) => DAYS.indexOf(day as (typeof DAYS)[number]));
  const ranges: string[] = [];
  let start = 0;
  for (let i = 1; i <= indexes.length; i++) {
    if (i === indexes.length || indexes[i] !== indexes[i - 1] + 1) {
      const first = DAYS[indexes[start]];
      const last = DAYS[indexes[i - 1]];
      ranges.push(i - 1 === start ? first : `${first}–${last}`);
      start = i;
    }
  }
  return ranges.join(", ");
}

const VERBS: Record<string, string> = { Deny: "Deny", RequireApproval: "Require an approval", Warn: "Warn" };

/** One sentence describing what the rule does with these values, e.g. "Deny if more than 0 Critical findings …". */
export function explainRule(model: RuleModel, effect: string): string {
  const verb = VERBS[effect] ?? effect;
  switch (model.type) {
    case "RequireTestsPassed":
      return `${verb} unless the artifact's build and tests both passed in CI.`;
    case "RequireImageDigest":
      return `${verb} unless the artifact has an immutable image digest (sha256:…).`;
    case "RequireScan": {
      if (model.scanners.length === 0) return `${verb}: no scanners are selected, so this rule always fails.`;
      const age = model.maxAgeHours.trim() === "" ? "" : ` in the last ${hours(model.maxAgeHours)}`;
      const scanners = ordered(model.scanners, SCANNERS);
      const each = scanners.length === 1 ? "has" : "each have";
      return `${verb} unless ${joinOr(scanners, "and")} ${each} a completed scan${age}.`;
    }
    case "MaxFindings": {
      const severity = model.severity === "Critical" ? "Critical" : `${model.severity}-or-higher`;
      const source = model.scanner === "" ? "any scanner" : model.scanner;
      const unknown = model.includeUnknown ? " (counting Unknown severity)" : "";
      return `${verb} if there are more than ${model.max || "0"} open ${severity} findings from ${source}${unknown}.`;
    }
    case "RequireApprovals": {
      const count = Number(model.count) || 1;
      const who =
        model.roles.length === 0
          ? "anyone allowed to approve deployments"
          : `users with the ${joinOr(ordered(model.roles, APPROVER_ROLES))} role`;
      return `Require ${count} ${count === 1 ? "approval" : "approvals"} from ${who}; the requester cannot approve.`;
    }
    case "AllowedBranches":
      return model.patterns.length === 0
        ? `${verb}: no branch patterns are set, so every branch fails.`
        : `${verb} unless the branch matches ${joinOr(model.patterns.map((pattern) => pattern.trim() || "(empty)"))}.`;
    case "DeploymentWindow": {
      if (model.allowed.length === 0) return `${verb}: no windows are set, so every request is outside the window.`;
      const windows = model.allowed.map(
        (slot) => `${dayRanges(slot.days) || "no days"} ${clockLabel(slot.from)}–${clockLabel(slot.to)}`,
      );
      return `${verb} outside ${joinOr(windows, "and")} (${model.timezone}).`;
    }
    case "RequirePriorEnvironment": {
      const age = model.maxAgeHours.trim() === "" ? "" : ` in the last ${hours(model.maxAgeHours)}`;
      return `${verb} unless the same artifact was deployed successfully to ${model.tier}${age}.`;
    }
  }
}

/** IANA time zones from the browser, with a short fallback list for older engines. */
export function timeZones(current: string): string[] {
  let zones: string[] = [];
  try {
    zones = Intl.supportedValuesOf("timeZone");
  } catch {
    zones = [];
  }
  if (zones.length === 0) {
    zones = [
      "Asia/Kuala_Lumpur",
      "Asia/Singapore",
      "Asia/Jakarta",
      "Asia/Tokyo",
      "Asia/Kolkata",
      "Australia/Sydney",
      "Europe/London",
      "Europe/Berlin",
      "America/New_York",
      "America/Los_Angeles",
    ];
  }
  const all = new Set(zones);
  all.add("UTC");
  if (current) all.add(current);
  return [...all].sort((a, b) => a.localeCompare(b));
}
