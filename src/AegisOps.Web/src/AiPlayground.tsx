import { useEffect, useState } from "react";
import { api, ApiError } from "./api";
import { GovernancePanel, type GovernanceState, type RateLimitRemaining } from "./GovernancePanel";
import { IconInfo, IconSparkles } from "./icons";
import { Alert, CardHeader, Spinner } from "./ui";

type AiModel = { id: string; name: string; displayName?: string | null; provider: string };
type ChatMessage = { role: "user" | "assistant"; content: string };
type ChatResponse = {
  id: string;
  model?: string | null;
  message: { role: string; content: string | null };
  usage?: { promptTokens?: number | null; completionTokens?: number | null; latencyMs?: number };
  governance?: {
    policy?: string | null;
    sensitiveData?: { action?: string; detected?: string[] | null; redactions?: number };
    rateLimit?: RateLimitRemaining | null;
  };
};

// POST /api/v1/ai/chat accepts at most 32 messages per request.
const maxMessages = 32;

/** Maps a gateway rejection (docs/08 section 6) to a message the user can act on. */
function describeFailure(status: number, reason: string | undefined, title: string): string {
  switch (status) {
    case 400:
      return "The prompt was rejected by the AI policy: it is too long, matches a blocked pattern, or is empty.";
    case 403:
      return reason === "ModelNotAllowed"
        ? "The selected model is not allowed by your AI policy. Choose another model."
        : "You do not have permission to use the AI playground.";
    case 422:
      return "Blocked: the prompt contains sensitive data and your AI policy blocks it. Remove the sensitive data and try again.";
    case 429:
      return "Rate limit reached for your AI policy. Wait a moment and try again.";
    case 503:
      return `The AI service is unavailable${reason ? ` (${reason})` : ""}. Try again later.`;
    default:
      return title || "The AI request failed.";
  }
}

export function AiPlayground({ token }: { token: string }) {
  const [models, setModels] = useState<AiModel[]>([]);
  const [model, setModel] = useState("");
  const [modelsLoading, setModelsLoading] = useState(true);
  const [modelsError, setModelsError] = useState<string | null>(null);
  // Conversation state lives only in memory: prompts are never written to localStorage or sessionStorage.
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [draft, setDraft] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [governance, setGovernance] = useState<GovernanceState | null>(null);

  useEffect(() => {
    let active = true;
    api<AiModel[]>("/api/v1/ai/models", token)
      .then((result) => {
        if (!active) return;
        setModels(result);
        setModel((current) => (result.some((item) => item.name === current) ? current : (result[0]?.name ?? "")));
      })
      .catch((error: Error) => {
        if (active) setModelsError(error.message);
      })
      .finally(() => {
        if (active) setModelsLoading(false);
      });
    return () => {
      active = false;
    };
  }, [token]);

  async function send() {
    const content = draft.trim();
    if (!content || !model || sending) return;
    const outgoing = [...messages, { role: "user" as const, content }].slice(-maxMessages);
    setSending(true);
    setSendError(null);
    try {
      const result = await api<ChatResponse>("/api/v1/ai/chat", token, {
        method: "POST",
        body: JSON.stringify({ model, messages: outgoing }),
      });
      const sensitive = result.governance?.sensitiveData;
      setMessages([...outgoing, { role: "assistant", content: result.message.content ?? "" }]);
      setDraft("");
      setGovernance({
        outcome: "Allowed",
        status: 200,
        requestId: result.id,
        model: result.model ?? model,
        policyId: result.governance?.policy ?? null,
        action: sensitive?.action,
        detected: sensitive?.detected ?? [],
        redactions: sensitive?.redactions ?? 0,
        rateLimit: result.governance?.rateLimit ?? null,
      });
    } catch (error) {
      // The draft is kept so the user can edit and resend; a rejected prompt is not added to the history.
      if (error instanceof ApiError) {
        const reason = error.problem.reason;
        setSendError(describeFailure(error.status, reason, error.message));
        setGovernance({
          outcome: error.status === 503 ? "Failed" : "Blocked",
          status: error.status,
          requestId: error.problem.requestId,
          model,
          action: error.status === 422 ? "Block" : undefined,
          detected: [],
          redactions: null,
          blockReason: reason ?? error.message,
          rateLimit: null,
          rateLimited: error.status === 429,
        });
      } else {
        setSendError("The AI gateway could not be reached. Check your connection and try again.");
      }
    } finally {
      setSending(false);
    }
  }

  const canSend = !!model && !!draft.trim() && !sending;
  return (
    <div className="split split-aside">
      <section className="card chat-card" aria-labelledby="playground-heading">
        <CardHeader
          id="playground-heading"
          title="Chat"
          meta={messages.length === 1 ? "1 message" : `${messages.length} messages`}
        />
        <div className="chat-toolbar">
          <label className="field" htmlFor="ai-model-select">
            Model
            {modelsLoading ? (
              <span role="status" className="subtle">
                Loading allowed models…
              </span>
            ) : (
              <select
                id="ai-model-select"
                value={model}
                disabled={!models.length || sending}
                onChange={(event) => setModel(event.target.value)}
              >
                {models.map((item) => (
                  <option key={item.id} value={item.name}>
                    {item.displayName || item.name} · {item.provider}
                  </option>
                ))}
              </select>
            )}
          </label>
          {modelsError && <Alert>Could not load allowed models: {modelsError}</Alert>}
          {!modelsLoading && !modelsError && !models.length && (
            <Alert tone="warning">No models are allowed by your AI policy. Ask an administrator to allow one.</Alert>
          )}
          <p className="note" role="note">
            <IconInfo size={14} />
            <span>
              Prompts are inspected for sensitive data before they reach the model and may be logged as redacted
              previews in the AI request log. This conversation is kept only on this page and is not saved in your
              browser.
            </span>
          </p>
        </div>

        <ol className="chat-history" aria-label="Conversation" aria-live="polite">
          {messages.map((message, index) => (
            <li key={index} className={`chat-message chat-message-${message.role}`}>
              <span className="chat-author">{message.role === "user" ? "You" : "Assistant"}</span>
              <p>{message.content}</p>
              {message.role === "assistant" && (
                <small className="ai-label">
                  <IconSparkles size={12} />
                  AI-generated — verify before acting.
                </small>
              )}
            </li>
          ))}
          {!messages.length && <li className="chat-empty">No messages yet. Ask something below.</li>}
        </ol>

        <form
          className="chat-form"
          onSubmit={(event) => {
            event.preventDefault();
            void send();
          }}
        >
          <label className="field" htmlFor="ai-prompt">
            Prompt
            <textarea
              id="ai-prompt"
              rows={3}
              value={draft}
              disabled={sending}
              onChange={(event) => setDraft(event.target.value)}
            />
          </label>
          {sendError && <Alert>{sendError}</Alert>}
          <div className="form-actions">
            <button className="btn btn-primary" type="submit" disabled={!canSend} aria-busy={sending}>
              {sending ? (
                <>
                  <Spinner />
                  Sending…
                </>
              ) : (
                "Send"
              )}
            </button>
            <button
              className="btn btn-ghost"
              type="button"
              disabled={sending || (!messages.length && !governance)}
              onClick={() => {
                setMessages([]);
                setGovernance(null);
                setSendError(null);
              }}
            >
              Clear conversation
            </button>
          </div>
        </form>
      </section>
      <GovernancePanel governance={governance} />
    </div>
  );
}
