import { useEffect, useMemo, useState, type ReactNode } from "react";
import { ApiError, api } from "./api";
import { IconPlus, IconPolicies, IconX } from "./icons";
import { humanize } from "./format";
import { Alert, EmptyState, StatusBadge } from "./ui";
import {
  APPROVER_ROLES,
  DAYS,
  LIMITS,
  NEW_WINDOW,
  SCANNERS,
  SEVERITIES,
  TIERS,
  canonical,
  explainRule,
  formProblems,
  hasParameters,
  parseRule,
  serializeRule,
  timeZones,
  type RuleModel,
  type WindowSlot,
} from "./policyRules";

type PolicyRule = { id?: string; type: string; effect: string; order: number; isEnabled?: boolean; parameters: object };
type PolicyItem = {
  id: string;
  name: string;
  description: string | null;
  version: number;
  scope: string;
  scopeId: string | null;
  isEnabled: boolean;
  appliesToTiers: string[];
  rules: PolicyRule[];
};
type RuleTemplate = { type: string; effect: string; parameters: object };

export function Policies({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  const [items, setItems] = useState<PolicyItem[]>([]);
  const [catalogue, setCatalogue] = useState<RuleTemplate[]>([]);
  const [choice, setChoice] = useState("");
  const [savingPolicyId, setSavingPolicyId] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([
      api<PolicyItem[]>("/api/v1/policies", token),
      api<RuleTemplate[]>("/api/v1/policies/rule-types", token),
    ])
      .then(([policies, types]) => {
        setItems(policies);
        setCatalogue(types);
        setChoice(types[0]?.type ?? "");
      })
      .catch((error: Error) => onError(error.message));
  }, [token, onError]);

  /** PUT replaces every rule of the policy, so the other rules are sent with their stored parameters. */
  async function saveRules(policy: PolicyItem, rules: PolicyRule[]) {
    setSavingPolicyId(policy.id);
    try {
      await api(`/api/v1/policies/${policy.id}`, token, {
        method: "PUT",
        body: JSON.stringify({
          name: policy.name,
          description: policy.description,
          scope: policy.scope,
          scopeId: policy.scopeId,
          appliesToTiers: policy.appliesToTiers,
          rules: rules.map(({ type, effect, order, parameters }) => ({ type, effect, order, parameters })),
        }),
      });
      setItems(await api<PolicyItem[]>("/api/v1/policies", token));
    } finally {
      setSavingPolicyId(null);
    }
  }

  async function addRule(policy: PolicyItem) {
    const template = catalogue.find((item) => item.type === choice);
    if (!template) return;
    const order = policy.rules.reduce((next, rule) => Math.max(next, rule.order + 1), 0);
    await saveRules(policy, [
      ...policy.rules,
      { type: template.type, effect: template.effect, order, parameters: template.parameters },
    ]);
    onError(null);
  }

  return (
    <div className="stack-lg">
      <section className="card toolbar">
        <label className="field">
          Rule to add
          <select value={choice} onChange={(event) => setChoice(event.target.value)}>
            {catalogue.map((item) => (
              <option key={item.type} value={item.type}>
                {humanize(item.type)}
              </option>
            ))}
          </select>
        </label>
        <p className="field-hint toolbar-hint">
          Pick a rule type, then choose Add rule on the policy it belongs to. New rules start with safe defaults.
        </p>
      </section>
      {items.length === 0 && (
        <section className="card">
          <EmptyState icon={<IconPolicies />}>No policies yet.</EmptyState>
        </section>
      )}
      {items.map((policy) => (
        <section key={policy.id} className="card" aria-labelledby={`policy-${policy.id}`}>
          <div className="card-header">
            <div className="stack stack-tight">
              <div className="card-title">
                <h2 id={`policy-${policy.id}`} className="policy-heading">
                  {policy.name} <span>v{policy.version}</span>
                </h2>
                {!policy.isEnabled && <StatusBadge status="Disabled" tone="neutral" />}
              </div>
              <div className="row">
                <span className="tag">{policy.scope}</span>
                {policy.appliesToTiers.map((tier) => (
                  <span key={tier} className="tag">
                    {tier}
                  </span>
                ))}
                {policy.description && <span className="subtle policy-description">{policy.description}</span>}
              </div>
            </div>
            <button
              className="btn btn-secondary btn-sm"
              type="button"
              disabled={savingPolicyId === policy.id}
              onClick={() => void addRule(policy).catch((error: Error) => onError(error.message))}
            >
              <IconPlus size={14} />
              Add rule
            </button>
          </div>
          {policy.rules.length === 0 ? (
            <p className="empty-row">No rules in this policy.</p>
          ) : (
            <ol className="list">
              {policy.rules.map((rule, index) => (
                <RuleCard
                  key={`${policy.id}:${rule.order}`}
                  idPrefix={`rule-${policy.id}-${rule.order}`}
                  position={index + 1}
                  rule={rule}
                  saving={savingPolicyId === policy.id}
                  onSave={(parameters) =>
                    saveRules(
                      policy,
                      policy.rules.map((current) =>
                        current.order === rule.order ? { ...current, parameters } : current,
                      ),
                    )
                  }
                />
              ))}
            </ol>
          )}
        </section>
      ))}
    </div>
  );
}

type Draft = { base: string; model: RuleModel | null; json: string | null };
type SaveError = { title: string; detail?: string };

function RuleCard({
  idPrefix,
  position,
  rule,
  saving,
  onSave,
}: {
  idPrefix: string;
  position: number;
  rule: PolicyRule;
  saving: boolean;
  onSave: (parameters: object) => Promise<void>;
}) {
  const stored = useMemo(() => rule.parameters ?? {}, [rule.parameters]);
  const storedKey = canonical(stored);
  const parsed = useMemo(() => parseRule(rule.type, stored), [rule.type, stored]);
  const baseline = useMemo(() => (parsed.ok ? serializeRule(parsed.model) : stored), [parsed, stored]);
  const [draftState, setDraft] = useState<Draft | null>(null);
  const [error, setError] = useState<SaveError | null>(null);
  // A draft made against older stored parameters (another save reloaded the policy) is discarded.
  const draft = draftState?.base === storedKey ? draftState : null;

  const model = draft?.model ?? (parsed.ok ? parsed.model : null);
  const jsonOpen = draft ? draft.json !== null : !parsed.ok;
  const jsonText = draft?.json ?? JSON.stringify(stored, null, 2);
  const label = `${humanize(rule.type)} rule ${position}`;
  const titleId = `${idPrefix}-title`;

  let body: object | null = null;
  let jsonProblem: string | null = null;
  if (jsonOpen) {
    try {
      const value: unknown = JSON.parse(jsonText);
      if (value === null || typeof value !== "object" || Array.isArray(value)) jsonProblem = "Enter a JSON object.";
      else body = value;
    } catch {
      jsonProblem = "The JSON is not valid.";
    }
  } else if (model) {
    body = serializeRule(model);
  }
  const problems = !jsonOpen && model ? formProblems(model) : {};
  const dirty = jsonOpen
    ? body === null
      ? jsonText !== JSON.stringify(stored, null, 2)
      : canonical(body) !== storedKey && canonical(body) !== canonical(baseline)
    : body !== null && canonical(body) !== canonical(baseline);
  const blocked = jsonOpen ? jsonProblem !== null : Object.keys(problems).length > 0;
  const editable = hasParameters(rule.type) || !parsed.ok;

  function update(next: RuleModel) {
    setDraft({ base: storedKey, model: next, json: null });
  }

  function openJson() {
    setDraft({
      base: storedKey,
      model,
      json: JSON.stringify(model ? serializeRule(model) : stored, null, 2),
    });
  }

  function closeJson() {
    if (body === null) {
      setError({ title: `${jsonProblem ?? "The JSON is not valid."} Fix it or choose Cancel to go back.` });
      return;
    }
    const result = parseRule(rule.type, body);
    if (!result.ok) {
      setError({ title: `The form cannot show these parameters: ${result.reason} Fix the JSON or choose Cancel.` });
      return;
    }
    setError(null);
    setDraft({ base: storedKey, model: result.model, json: null });
  }

  function cancel() {
    setDraft(null);
    setError(null);
  }

  async function save() {
    if (body === null) return;
    setError(null);
    try {
      await onSave(body);
      setDraft(null);
    } catch (caught) {
      if (caught instanceof ApiError) setError({ title: caught.message, detail: caught.problem.detail });
      else setError({ title: caught instanceof Error ? caught.message : "The rule could not be saved." });
    }
  }

  return (
    <li className="policy-rule" aria-labelledby={titleId}>
      <span className="rule-order" aria-hidden="true">
        {position}
      </span>
      <div className="policy-rule-body">
        <div className="rule-head">
          <h3 id={titleId} className="rule-title">
            {humanize(rule.type)}
          </h3>
          <StatusBadge status={rule.effect} />
          {rule.isEnabled === false && <StatusBadge status="Disabled" tone="neutral" />}
          {dirty && <span className="rule-dirty">Unsaved changes</span>}
        </div>
        <p className="rule-summary">
          {model && !jsonOpen
            ? explainRule(model, rule.effect)
            : parsed.ok
              ? explainRule(parsed.model, rule.effect)
              : "Parameters are edited as JSON below."}
        </p>
        {!parsed.ok && !draft && (
          <Alert tone="warning" role="note" title="Shown as JSON">
            {parsed.reason} The form would drop or change these values, so edit the JSON directly. Nothing is changed
            until you save.
          </Alert>
        )}
        {editable && (
          <>
            {jsonOpen ? (
              <label className="field rule-json">
                Parameters (JSON)
                <textarea
                  className="textarea-code"
                  aria-label={`${label} parameters JSON`}
                  spellCheck={false}
                  rows={Math.min(14, Math.max(3, jsonText.split("\n").length))}
                  value={jsonText}
                  onChange={(event) => setDraft({ base: storedKey, model, json: event.target.value })}
                />
                {jsonProblem && dirty && <span className="field-error">{jsonProblem}</span>}
              </label>
            ) : (
              model && <RuleForm idPrefix={idPrefix} model={model} problems={problems} onChange={update} />
            )}
            {error && (
              <Alert title={error.title} onDismiss={() => setError(null)}>
                {error.detail}
              </Alert>
            )}
            <div className="rule-actions">
              <div className="form-actions">
                <button
                  className="btn btn-primary btn-sm"
                  type="button"
                  aria-label={`Save ${label}`}
                  disabled={!dirty || blocked || saving}
                  onClick={() => void save()}
                >
                  Save
                </button>
                <button
                  className="btn btn-ghost btn-sm"
                  type="button"
                  aria-label={`Cancel ${label} changes`}
                  disabled={!dirty || saving}
                  onClick={cancel}
                >
                  Cancel
                </button>
              </div>
              {parsed.ok && (
                <button
                  className="btn btn-ghost btn-sm"
                  type="button"
                  aria-expanded={jsonOpen}
                  onClick={() => (jsonOpen ? closeJson() : openJson())}
                >
                  {jsonOpen ? "Back to form" : "Advanced: edit JSON"}
                </button>
              )}
            </div>
          </>
        )}
      </div>
    </li>
  );
}

function FieldError({ id, message }: { id: string; message?: string }) {
  return message ? (
    <span id={id} className="field-error">
      {message}
    </span>
  ) : null;
}

function NumberField({
  id,
  label,
  value,
  min,
  max,
  hint,
  problem,
  onChange,
}: {
  id: string;
  label: string;
  value: string;
  min: number;
  max: number;
  hint?: string;
  problem?: string;
  onChange: (value: string) => void;
}) {
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input
        id={id}
        type="number"
        inputMode="numeric"
        min={min}
        max={max}
        step={1}
        value={value}
        aria-invalid={problem ? true : undefined}
        aria-describedby={problem ? `${id}-error` : hint ? `${id}-hint` : undefined}
        onChange={(event) => onChange(event.target.value)}
      />
      {problem ? (
        <FieldError id={`${id}-error`} message={problem} />
      ) : (
        hint && (
          <span id={`${id}-hint`} className="field-hint">
            {hint}
          </span>
        )
      )}
    </div>
  );
}

function CheckboxGroup({
  legend,
  options,
  selected,
  problem,
  hint,
  onChange,
}: {
  legend: string;
  options: readonly string[];
  selected: string[];
  problem?: string;
  hint?: ReactNode;
  onChange: (values: string[]) => void;
}) {
  return (
    <fieldset className="checkbox-group rule-wide">
      <legend>{legend}</legend>
      <div className="checkbox-grid">
        {options.map((option) => (
          <label key={option} className="checkbox">
            <input
              type="checkbox"
              checked={selected.includes(option)}
              onChange={(event) =>
                onChange(event.target.checked ? [...selected, option] : selected.filter((item) => item !== option))
              }
            />
            {option}
          </label>
        ))}
      </div>
      {problem ? <span className="field-error">{problem}</span> : hint && <span className="field-hint">{hint}</span>}
    </fieldset>
  );
}

function RuleForm({
  idPrefix,
  model,
  problems,
  onChange,
}: {
  idPrefix: string;
  model: RuleModel;
  problems: Record<string, string>;
  onChange: (model: RuleModel) => void;
}) {
  const id = (name: string) => `${idPrefix}-${name}`;
  switch (model.type) {
    case "RequireTestsPassed":
    case "RequireImageDigest":
      return null;
    case "RequireScan":
      return (
        <div className="rule-form">
          <CheckboxGroup
            legend="Scanners that must have run"
            options={SCANNERS}
            selected={model.scanners}
            problem={problems.scanners}
            onChange={(scanners) => onChange({ ...model, scanners })}
          />
          <NumberField
            id={id("age")}
            label="Maximum scan age (hours)"
            value={model.maxAgeHours}
            min={LIMITS.ageHours[0]}
            max={LIMITS.ageHours[1]}
            hint="1–8760. Leave empty for no age limit."
            problem={problems.maxAgeHours}
            onChange={(maxAgeHours) => onChange({ ...model, maxAgeHours })}
          />
        </div>
      );
    case "MaxFindings":
      return (
        <div className="rule-form">
          <div className="field">
            <label htmlFor={id("severity")}>Severity</label>
            <select
              id={id("severity")}
              value={model.severity}
              aria-describedby={id("severity-hint")}
              onChange={(event) => onChange({ ...model, severity: event.target.value })}
            >
              {SEVERITIES.map((severity) => (
                <option key={severity} value={severity}>
                  {severity === "Critical" ? severity : `${severity} and above`}
                </option>
              ))}
            </select>
            <span id={id("severity-hint")} className="field-hint">
              This severity or higher.
            </span>
          </div>
          <NumberField
            id={id("max")}
            label="Maximum allowed"
            value={model.max}
            min={LIMITS.findings[0]}
            max={LIMITS.findings[1]}
            hint="0 allows none."
            problem={problems.max}
            onChange={(max) => onChange({ ...model, max })}
          />
          <div className="field">
            <label htmlFor={id("scanner")}>Scanner</label>
            <select
              id={id("scanner")}
              value={model.scanner}
              onChange={(event) => onChange({ ...model, scanner: event.target.value })}
            >
              <option value="">Any scanner</option>
              {SCANNERS.map((scanner) => (
                <option key={scanner} value={scanner}>
                  {scanner}
                </option>
              ))}
            </select>
          </div>
          <label className="checkbox rule-wide">
            <input
              type="checkbox"
              checked={model.includeUnknown}
              onChange={(event) => onChange({ ...model, includeUnknown: event.target.checked })}
            />
            Also count findings with Unknown severity
          </label>
        </div>
      );
    case "RequireApprovals":
      return (
        <div className="rule-form">
          <NumberField
            id={id("count")}
            label="Approvals required"
            value={model.count}
            min={LIMITS.approvals[0]}
            max={LIMITS.approvals[1]}
            hint="1–10. The requester never counts."
            problem={problems.count}
            onChange={(count) => onChange({ ...model, count })}
          />
          <CheckboxGroup
            legend="Who can approve"
            options={APPROVER_ROLES}
            selected={model.roles}
            hint={model.roles.length === 0 ? "None selected: anyone allowed to approve deployments." : undefined}
            onChange={(roles) => onChange({ ...model, roles })}
          />
        </div>
      );
    case "AllowedBranches":
      return (
        <BranchList
          idPrefix={idPrefix}
          patterns={model.patterns}
          problems={problems}
          onChange={(patterns) => onChange({ ...model, patterns })}
        />
      );
    case "DeploymentWindow":
      return (
        <WindowEditor
          idPrefix={idPrefix}
          timezone={model.timezone}
          allowed={model.allowed}
          problems={problems}
          onChange={(next) => onChange({ ...model, ...next })}
        />
      );
    case "RequirePriorEnvironment":
      return (
        <div className="rule-form">
          <div className="field">
            <label htmlFor={id("tier")}>Must first succeed in</label>
            <select
              id={id("tier")}
              value={model.tier}
              onChange={(event) => onChange({ ...model, tier: event.target.value })}
            >
              {TIERS.map((tier) => (
                <option key={tier} value={tier}>
                  {tier}
                </option>
              ))}
            </select>
          </div>
          <NumberField
            id={id("age")}
            label="Within (hours)"
            value={model.maxAgeHours}
            min={LIMITS.ageHours[0]}
            max={LIMITS.ageHours[1]}
            hint="1–8760. Leave empty for any time."
            problem={problems.maxAgeHours}
            onChange={(maxAgeHours) => onChange({ ...model, maxAgeHours })}
          />
        </div>
      );
  }
}

function BranchList({
  idPrefix,
  patterns,
  problems,
  onChange,
}: {
  idPrefix: string;
  patterns: string[];
  problems: Record<string, string>;
  onChange: (patterns: string[]) => void;
}) {
  return (
    <fieldset className="checkbox-group pattern-group">
      <legend>Allowed branches</legend>
      <ul className="pattern-list">
        {patterns.map((pattern, index) => {
          const inputId = `${idPrefix}-pattern-${index}`;
          const problem = problems[`patterns.${index}`];
          return (
            <li key={index} className="pattern-row">
              <label className="sr-only" htmlFor={inputId}>
                Branch pattern {index + 1}
              </label>
              <input
                id={inputId}
                className="mono-input"
                value={pattern}
                placeholder="main or release/*"
                spellCheck={false}
                autoComplete="off"
                aria-invalid={problem ? true : undefined}
                aria-describedby={problem ? `${inputId}-error` : undefined}
                onChange={(event) =>
                  onChange(patterns.map((item, position) => (position === index ? event.target.value : item)))
                }
              />
              <button
                type="button"
                className="icon-button"
                aria-label={`Remove branch pattern ${index + 1}`}
                onClick={() => onChange(patterns.filter((_, position) => position !== index))}
              >
                <IconX />
              </button>
              <FieldError id={`${inputId}-error`} message={problem} />
            </li>
          );
        })}
      </ul>
      {problems.patterns && <span className="field-error">{problems.patterns}</span>}
      <div className="form-actions">
        <button
          type="button"
          className="btn btn-secondary btn-sm"
          disabled={patterns.length >= LIMITS.patterns}
          onClick={() => onChange([...patterns, ""])}
        >
          <IconPlus size={14} />
          Add pattern
        </button>
        <span className="field-hint">
          <code>*</code> matches any characters; everything else is literal and case-sensitive.
        </span>
      </div>
    </fieldset>
  );
}

function WindowEditor({
  idPrefix,
  timezone,
  allowed,
  problems,
  onChange,
}: {
  idPrefix: string;
  timezone: string;
  allowed: WindowSlot[];
  problems: Record<string, string>;
  onChange: (next: { timezone?: string; allowed?: WindowSlot[] }) => void;
}) {
  const zones = useMemo(() => timeZones(timezone), [timezone]);
  const setSlot = (index: number, slot: WindowSlot) =>
    onChange({ allowed: allowed.map((item, position) => (position === index ? slot : item)) });
  return (
    <div className="window-editor">
      <div className="field window-zone">
        <label htmlFor={`${idPrefix}-zone`}>Time zone</label>
        <select
          id={`${idPrefix}-zone`}
          value={timezone}
          onChange={(event) => onChange({ timezone: event.target.value })}
        >
          {zones.map((zone) => (
            <option key={zone} value={zone}>
              {zone}
            </option>
          ))}
        </select>
      </div>
      <ul className="window-list">
        {allowed.map((slot, index) => {
          const timeProblem = problems[`allowed.${index}.time`];
          const dayProblem = problems[`allowed.${index}.days`];
          return (
            <li key={index}>
              <fieldset className="window-row">
                <legend className="sr-only">Window {index + 1}</legend>
                <fieldset className="day-picker">
                  <legend className="sr-only">Window {index + 1} days</legend>
                  {DAYS.map((day) => (
                    <label key={day} className="day-chip">
                      <input
                        type="checkbox"
                        checked={slot.days.includes(day)}
                        onChange={(event) =>
                          setSlot(index, {
                            ...slot,
                            days: event.target.checked ? [...slot.days, day] : slot.days.filter((item) => item !== day),
                          })
                        }
                      />
                      {day}
                    </label>
                  ))}
                </fieldset>
                <div className="field window-time">
                  <label htmlFor={`${idPrefix}-from-${index}`}>From</label>
                  <input
                    id={`${idPrefix}-from-${index}`}
                    type="time"
                    value={timeInput(slot.from)}
                    aria-invalid={timeProblem ? true : undefined}
                    onChange={(event) => setSlot(index, { ...slot, from: event.target.value })}
                  />
                </div>
                <div className="field window-time">
                  <label htmlFor={`${idPrefix}-to-${index}`}>Until</label>
                  <input
                    id={`${idPrefix}-to-${index}`}
                    type="time"
                    value={timeInput(slot.to)}
                    aria-invalid={timeProblem ? true : undefined}
                    onChange={(event) => setSlot(index, { ...slot, to: event.target.value })}
                  />
                </div>
                <button
                  type="button"
                  className="icon-button window-remove"
                  aria-label={`Remove window ${index + 1}`}
                  onClick={() => onChange({ allowed: allowed.filter((_, position) => position !== index) })}
                >
                  <IconX />
                </button>
                {(dayProblem || timeProblem) && (
                  <span className="field-error window-error">
                    {[dayProblem, timeProblem].filter(Boolean).join(" ")}
                  </span>
                )}
              </fieldset>
            </li>
          );
        })}
      </ul>
      {problems.allowed && <span className="field-error">{problems.allowed}</span>}
      <div className="form-actions">
        <button
          type="button"
          className="btn btn-secondary btn-sm"
          disabled={allowed.length >= LIMITS.windows}
          onClick={() => onChange({ allowed: [...allowed, { ...NEW_WINDOW, days: [...NEW_WINDOW.days] }] })}
        >
          <IconPlus size={14} />
          Add window
        </button>
        <span className="field-hint">Requests outside every window fail. Times are inclusive.</span>
      </div>
    </div>
  );
}

/** `<input type="time">` takes HH:mm or HH:mm:ss; stored values may have a fraction (23:59:59.9999999). */
function timeInput(clock: string) {
  const match = /^(\d{1,2}):(\d{2})(?::(\d{2}))?/.exec(clock);
  if (!match) return "";
  return `${match[1].padStart(2, "0")}:${match[2]}${match[3] ? `:${match[3]}` : ""}`;
}
