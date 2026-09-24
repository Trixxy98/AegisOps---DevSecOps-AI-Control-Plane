# 08 — AI Gateway

AegisOps is not "a chat UI for Ollama". It is a **governed proxy**: every AI request passes
through authentication, policy, rate limiting, content inspection and audit before it is
allowed to reach a model — and the same pipeline is used by AegisOps' own AI features.

## 1. Pipeline

```mermaid
flowchart LR
    A[Request<br/>POST /ai/chat] --> B[Authenticate<br/>JWT]
    B --> C[Authorize<br/>permission ai:chat]
    C --> D[Resolve AiPolicy<br/>Team → Global]
    D --> E{Model<br/>allowed?}
    E -- no --> X1[403 Blocked<br/>reason=ModelNotAllowed]
    E -- yes --> F{Rate limit<br/>Redis sliding window}
    F -- exceeded --> X2[429 Blocked<br/>reason=RateLimited]
    F -- ok --> G[Prompt validation<br/>length · blocked patterns]
    G -- invalid --> X3[400 Blocked<br/>reason=PromptRejected]
    G -- ok --> H[Sensitive-data detection]
    H -- Block --> X4[422 Blocked<br/>reason=SensitiveData]
    H -- Redact --> I[Redacted prompt]
    H -- Allow --> I
    I --> J[ILlmClient → Ollama]
    J --> K[Response post-check<br/>optional secret scan]
    K --> L[Persist AiRequest<br/>+ AuditEvent + metrics]
    L --> M[Response + governance metadata]
    X1 & X2 & X3 & X4 --> L
```

Every path — including blocked ones — ends in an `AiRequest` row. Blocked requests are
first-class data for security review.

## 2. Effective policy resolution

`AiPolicy` scopes: `Global` (exactly one enabled required, seeded) and `Team`. The effective
policy for a user is the enabled **Team** policy of the team selected in the request
(`teamId`, defaults to the user's first team) if it exists, otherwise the **Global** policy.
Unlike deployment policies, AI policies are *not* merged — the most specific one wins,
because rate limits and model lists cannot be meaningfully unioned.

Admins can bypass nothing: policy applies to all roles. Only the `Admin` role may edit the
Global policy; `Security` may edit Team policies.

## 3. Controls

| Control | Configuration (`AiPolicy`) | Implementation |
| --- | --- | --- |
| **Model allow-list** | `AllowedModelIds[]` | Model must exist, be `IsEnabled`, and be in list |
| **Rate limit per minute / day** | `RequestsPerMinute` (default 20), `RequestsPerDay` (default 500) | Redis sorted-set sliding window per `(userId)`; key `ai:rl:m:{userId}`, `ai:rl:d:{userId}`; atomic Lua script `ZREMRANGEBYSCORE + ZCARD + ZADD + EXPIRE` |
| **Prompt size** | `MaxPromptChars` (default 16 000) | Sum of message contents |
| **Blocked patterns** | `BlockedPatterns[]` (regex) | e.g. `(?i)ignore (all|previous) instructions`, jailbreak boilerplate; hit → `PromptRejected` |
| **Sensitive data** | `SensitiveDataAction: Allow \| Redact \| Block` | `ISensitiveDataDetector` (§4) |
| **Content retention** | `StoreContent` (default `false`) | If false, only `PromptHash` + redacted 500-char previews are stored |
| **Response checks** | `Ai:ScanResponses` (app setting) | Run the secret detectors on the completion; log `ResponseContainedSecret` metric |
| **Timeouts** | `Ai:Ollama:TimeoutSeconds` (default 120) | `HttpClient` timeout + `CancellationToken` from request |

## 4. Sensitive-data detection

`RegexSensitiveDataDetector` with a curated, unit-tested pattern set. Each detector returns
`type`, `span`, `confidence`. Redaction replaces the span with `[REDACTED:<TYPE>]`.

| Type | Pattern summary | Default confidence |
| --- | --- | --- |
| `AWS_ACCESS_KEY` | `AKIA[0-9A-Z]{16}` | High |
| `AWS_SECRET_KEY` | 40-char base64 near `aws_secret` keywords | Medium |
| `GITHUB_TOKEN` | `gh[pousr]_[A-Za-z0-9]{36,}` | High |
| `GENERIC_API_KEY` | `(api[_-]?key|secret|token)\s*[:=]\s*['"]?[A-Za-z0-9_\-]{20,}` | Medium |
| `JWT` | `eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+` | High |
| `PRIVATE_KEY` | `-----BEGIN (RSA \|EC \|OPENSSH )?PRIVATE KEY-----` | High |
| `CONNECTION_STRING` | `(Password|Pwd)=[^;]+;` or `postgres://user:pass@` | High |
| `EMAIL` | RFC-5322-lite | Low (redact only if policy `RedactPii = true`) |
| `CREDIT_CARD` | 13–19 digits passing Luhn | High |
| `MY_NRIC` | `\b\d{6}-\d{2}-\d{4}\b` (Malaysian IC) | Medium |
| `PHONE_MY` | `\b(\+?6?01)[0-9]{8,9}\b` | Low |
| `IPV4_PRIVATE` | RFC-1918 ranges | Low (network-team relevance; redact optional) |

Detectors are pluggable (`IEnumerable<ISensitiveDataDetector>`). V2: entropy-based secret
detection, `Presidio`-style NER via a small local model.

## 5. Ollama integration

`ILlmClient` (Application port) → `OllamaLlmClient` (Infrastructure) using Ollama's HTTP API:

| Operation | Ollama endpoint | Notes |
| --- | --- | --- |
| Chat | `POST /api/chat` | `stream: false` in v1 (simpler audit); streaming via SSE is V2 |
| List models | `GET /api/tags` | Used by Admin "Sync models" to populate `AiModel` |
| Health | `GET /` | Health check contributor `ollama` (degraded, not unhealthy) |

Configuration: `Ai:Ollama:BaseUrl` (`http://ollama:11434` in Compose), `Ai:Ollama:TimeoutSeconds`.
The Compose stack includes a one-shot `ollama-init` service that runs `ollama pull` for the
default models.

### Default models (PROPOSED)

| Model | Use | Size | Why |
| --- | --- | --- | --- |
| `llama3.2:3b` | General chat, deployment summaries | ~2 GB | Runs on 8 GB RAM laptops without GPU |
| `qwen2.5-coder:7b` | Findings explanation, code-related prompts | ~4.7 GB | Better at code; optional, only if hardware allows |

## 6. Endpoints

| Method & path | Purpose | Auth |
| --- | --- | --- |
| `POST /api/v1/ai/chat` | Governed chat completion | `ai:chat` |
| `GET /api/v1/ai/models` | Models the caller may use (filtered by effective policy) | `ai:chat` |
| `GET /api/v1/ai/requests` | Paginated AI request log (own requests; `Security`/`Admin` see all) | `ai:requests:read` |
| `GET /api/v1/ai/policies`, `PUT …/{id}` | Manage AI policies | `ai:policies:write` |
| `POST /api/v1/admin/ai/models/sync` | Sync `AiModel` from Ollama tags | `Admin` |
| `POST /api/v1/deployments/{id}/ai-summary` | AegisOps feature: summarize evaluation + findings for approvers | `deployments:read` + `ai:chat` |
| `POST /api/v1/findings/{id}/ai-explain` | Explain a finding and suggest a fix | `findings:read` + `ai:chat` |

### `POST /ai/chat` request

```json
{
  "model": "llama3.2:3b",
  "teamId": "0190…",
  "messages": [
    { "role": "system", "content": "You are a helpful DevOps assistant." },
    { "role": "user", "content": "Explain this error: ... AKIAIOSFODNN7EXAMPLE ..." }
  ],
  "options": { "temperature": 0.2, "maxTokens": 1024 }
}
```

### Response

```json
{
  "id": "0190…",
  "model": "llama3.2:3b",
  "message": { "role": "assistant", "content": "..." },
  "usage": { "promptTokens": 212, "completionTokens": 318, "latencyMs": 4210 },
  "governance": {
    "policy": "Network team AI policy v3",
    "sensitiveData": { "action": "Redact", "detected": ["AWS_ACCESS_KEY"], "redactions": 1 },
    "rateLimit": { "remainingMinute": 12, "remainingDay": 488 }
  }
}
```

Blocked responses use RFC 9457 Problem Details with `type =
https://aegisops.dev/problems/ai-blocked` and an `extensions.reason` field.

## 7. Built-in AI features (using the same gateway)

| Feature | Prompt built from | Value for the story |
| --- | --- | --- |
| **Deployment summary** | Policy evaluation JSON + top findings + artifact metadata | Approvers get a 5-line plain-English summary: "2 HIGH CVEs in `libcurl`, fixed in 8.9.1; staging healthy 2h" |
| **Finding explanation** | Rule id, description, file/package | Developers understand and fix faster |

Both run with `Purpose = DeploymentSummary | FindingsExplanation`, are rate-limited under
the same policy and appear in the same `AiRequest` log. Outputs are labelled *"AI-generated
— verify before acting"* in the UI and never influence the policy decision.

## 8. Metrics (see [15 Observability](15-observability.md))

`aegisops_ai_requests_total{model,status,reason}`, `aegisops_ai_request_latency_seconds{model}`,
`aegisops_ai_tokens_total{model,kind}`, `aegisops_ai_sensitive_detections_total{type,action}`.

## 9. V2 candidates

Streaming responses (SSE), tool/function-call permissions per role, OpenAI-compatible
adapter for `ILlmClient`, per-project budgets (token quotas), prompt templates library,
response-content policies.
