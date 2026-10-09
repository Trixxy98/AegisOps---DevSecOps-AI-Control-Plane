using System.Text.Json;

namespace AegisOps.Domain.Policy;

public sealed class PolicyRule {
    public Guid Id {get; private set;}
    public Guid PolicyId {get; private set;}
    public PolicyRuleType Type {get; private set;}
    public RuleEffect Effect {get; private set;}
    public string Parameters {get; private set;}
    public int Order {get; private set;}
    public bool IsEnabled {get; private set;}

    private PolicyRule() {
        Parameters = "{}";
    }

    public static PolicyRule Create(
        Guid policyId,
        PolicyRuleType type,
        RuleEffect effect,
        int order,
        string? parameters = null
    ) {
        if (policyId == Guid.Empty) {
            throw new ArgumentException("Policy ID is required.", nameof(policyId));
        }

        if (!Enum.IsDefined(type)) {
            throw new ArgumentException("Policy rule type is invalid.", nameof(type));
        }

        if (!Enum.IsDefined(effect)) {
            throw new ArgumentException("Rule effect is invalid.", nameof(effect));
        }

        if (order < 0) {
            throw new ArgumentException("Order must not be negative.", nameof(order));
        }

        return new PolicyRule {
            Id = Guid.CreateVersion7(),
            PolicyId = policyId,
            Type = type,
            Effect = effect,
            Parameters = NormalizeParameters(parameters),
            Order = order,
            IsEnabled = true,
        };
    }

    public void Replace(PolicyRuleType type, RuleEffect effect, string? parameters) {
        if (!Enum.IsDefined(type)) {
            throw new ArgumentException("Policy rule type is invalid.", nameof(type));
        }

        if (!Enum.IsDefined(effect)) {
            throw new ArgumentException("Rule effect is invalid.", nameof(effect));
        }

        Type = type;
        Effect = effect;
        Parameters = NormalizeParameters(parameters);
    }

    public void Reorder(int order) {
        if (order < 0) {
            throw new ArgumentException("Order must not be negative.", nameof(order));
        }

        Order = order;
    }

    public void Enable() {
        IsEnabled = true;
    }

    public void Disable() {
        IsEnabled = false;
    }

    private static string NormalizeParameters(string? parameters) {
        if (string.IsNullOrWhiteSpace(parameters)) {
            return "{}";
        }

        try {
            using var document = JsonDocument.Parse(parameters);
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                throw new ArgumentException("Parameters must be JSON object.", nameof(parameters));
            }
        } catch (JsonException exception) {
            throw new ArgumentException("Parameters must be a JSON object.", nameof(parameters), exception);
        }

        return parameters.Trim();
    }
}