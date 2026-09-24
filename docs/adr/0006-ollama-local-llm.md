# ADR-0006 — Ollama as the only AI provider behind a gateway abstraction

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

AegisOps must demonstrate **AI governance** (model access, rate limits, sensitive-data
handling, audit) without recurring cost and without sending data to third parties. The
value is in the gateway, not in the model quality.

## Decision

- Use **Ollama** as the sole LLM provider in v1, running in the Compose stack.
- Access it only through the Application port **`ILlmClient`** implemented by
  `OllamaLlmClient`; all governance logic sits in front of this port and is
  provider-agnostic.
- Default models (`PROPOSED`): `llama3.2:3b` for general chat and deployment summaries;
  `qwen2.5-coder:7b` optional for code-related prompts when hardware allows.
- Non-streaming responses in v1 to keep audit simple.

## Alternatives considered

| Alternative | Why not |
| --- | --- |
| OpenAI / Anthropic APIs | Cost; data leaves the machine; contradicts the "100% free, self-hosted" constraint |
| Multiple providers from day one | Governance logic does not change per provider; a second adapter is a small V2 task once the gateway exists |
| LangChain / Semantic Kernel | Orchestration frameworks add abstraction the gateway does not need; raw HTTP to Ollama is ~100 lines |
| Running models without Ollama (llama.cpp directly) | Ollama gives model management, an HTTP API and Docker image for free |

## Consequences

- Positive: zero cost; privacy story; deterministic demo environment.
- Negative: small models produce mediocre summaries; requires ~4–8 GB RAM; slow without
  GPU. AI features must degrade gracefully when Ollama is absent (health = Degraded,
  buttons disabled with explanation).
