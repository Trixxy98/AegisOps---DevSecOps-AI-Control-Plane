using System.Net.Http.Json;
using System.Text.Json;
using AegisOps.Application.Observability;

namespace AegisOps.Infrastructure.Ai;

public sealed class OllamaLlmClient(HttpClient http) : ILlmClient {
    public async Task<LlmReply> Chat(string model, IReadOnlyList<LlmMessage> messages, CancellationToken ct) {
        using var activity = AegisOpsTracing.Source.StartActivity("Ollama.Chat", System.Diagnostics.ActivityKind.Client);
        // Error until a reply is parsed; no exception details (they can include the endpoint URL).
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
        using var response = await http.PostAsJsonAsync("api/chat", new {
            model, stream = false,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        }, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        var reply = new LlmReply(root.GetProperty("message").GetProperty("content").GetString() ?? "",
            root.TryGetProperty("prompt_eval_count", out var prompt) ? prompt.GetInt32() : null,
            root.TryGetProperty("eval_count", out var generated) ? generated.GetInt32() : null);
        // Model names can be user-defined; don't export arbitrary names, prompts or responses.
        if (reply.PromptTokens is >= 0) activity?.SetTag("gen_ai.usage.input_tokens", reply.PromptTokens);
        if (reply.CompletionTokens is >= 0) activity?.SetTag("gen_ai.usage.output_tokens", reply.CompletionTokens);
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        return reply;
    }
    public async Task<IReadOnlyList<string>> ListModels(CancellationToken ct) {
        using var response = await http.GetAsync("api/tags", ct);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("models").EnumerateArray()
            .Select(model => model.GetProperty("name").GetString() ?? "")
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
    }
}
