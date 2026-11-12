import { useEffect, useState } from "react";
import { api } from "./api";
import { IconPlus, IconPolicies } from "./icons";
import { humanize } from "./format";
import { EmptyState, StatusBadge } from "./ui";

type PolicyRule = { type: string; effect: string; order: number; parameters: object };
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

export function Policies({ token, onError }: { token: string; onError: (value: string | null) => void }) {
  const [items, setItems] = useState<PolicyItem[]>([]);
  const [catalogue, setCatalogue] = useState<Array<{ type: string; effect: string; parameters: object }>>([]);
  const [choice, setChoice] = useState("");
  const [parameterDrafts, setParameterDrafts] = useState<Record<string, string>>({});
  const [savingPolicyId, setSavingPolicyId] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([
      api<PolicyItem[]>("/api/v1/policies", token),
      api<Array<{ type: string; effect: string; parameters: object }>>("/api/v1/policies/rule-types", token),
    ])
      .then(([policies, types]) => {
        setItems(policies);
        setCatalogue(types);
        setChoice(types[0]?.type ?? "");
      })
      .catch((error: Error) => onError(error.message));
  }, [token, onError]);

  async function saveRules(policy: PolicyItem, rules: PolicyRule[], clearDraftKey?: string) {
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
          rules,
        }),
      });
      setItems(await api<PolicyItem[]>("/api/v1/policies", token));
      if (clearDraftKey) {
        setParameterDrafts((current) => {
          const next = { ...current };
          delete next[clearDraftKey];
          return next;
        });
      }
      onError(null);
    } finally {
      setSavingPolicyId(null);
    }
  }

  async function addRule(policy: PolicyItem) {
    const template = catalogue.find((item) => item.type === choice);
    if (!template) {
      return;
    }
    const rules = [
      ...policy.rules,
      { type: template.type, effect: template.effect, order: policy.rules.length, parameters: template.parameters },
    ];
    await saveRules(policy, rules);
  }

  async function saveRuleParameters(policy: PolicyItem, rule: PolicyRule) {
    const key = `${policy.id}:${rule.order}`;
    const source = parameterDrafts[key] ?? JSON.stringify(rule.parameters ?? {}, null, 2);
    let parameters: object;
    try {
      const parsed: unknown = JSON.parse(source);
      if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) {
        throw new Error("Rule parameters must be a JSON object.");
      }
      parameters = parsed;
    } catch (error) {
      if (error instanceof Error && error.message === "Rule parameters must be a JSON object.") {
        throw error;
      }
      throw new Error(`${rule.type} parameters must be valid JSON.`);
    }

    const rules = policy.rules.map((current) => (current.order === rule.order ? { ...current, parameters } : current));
    await saveRules(policy, rules, key);
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
        <p className="field-hint toolbar-hint">Pick a rule type, then choose Add rule on the policy it belongs to.</p>
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
              {policy.rules.map((rule) => {
                const key = `${policy.id}:${rule.order}`;
                return (
                  <li className="policy-rule" key={`${rule.type}-${rule.order}`}>
                    <span className="rule-order" aria-hidden="true">
                      {rule.order + 1}
                    </span>
                    <div className="policy-rule-body">
                      <div className="row">
                        <strong>{humanize(rule.type)}</strong>
                        <StatusBadge status={rule.effect} />
                      </div>
                      <label className="field">
                        <span className="sr-only">
                          {policy.name} {humanize(rule.type)} parameters
                        </span>
                        <textarea
                          className="textarea-code"
                          aria-label={`${policy.name} ${humanize(rule.type)} parameters`}
                          spellCheck={false}
                          rows={Math.min(
                            12,
                            Math.max(
                              2,
                              (parameterDrafts[key] ?? JSON.stringify(rule.parameters ?? {}, null, 2)).split("\n")
                                .length,
                            ),
                          )}
                          value={parameterDrafts[key] ?? JSON.stringify(rule.parameters ?? {}, null, 2)}
                          onChange={(event) =>
                            setParameterDrafts((current) => ({ ...current, [key]: event.target.value }))
                          }
                        />
                      </label>
                      <div className="form-actions">
                        <button
                          className="btn btn-secondary btn-sm"
                          type="button"
                          aria-label={`Save ${humanize(rule.type)} parameters`}
                          disabled={savingPolicyId === policy.id}
                          onClick={() =>
                            void saveRuleParameters(policy, rule).catch((error: Error) => onError(error.message))
                          }
                        >
                          Save parameters
                        </button>
                      </div>
                    </div>
                  </li>
                );
              })}
            </ol>
          )}
        </section>
      ))}
    </div>
  );
}
