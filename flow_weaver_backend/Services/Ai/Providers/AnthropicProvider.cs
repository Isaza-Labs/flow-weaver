using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Services.Ai.Providers;

// Direct HTTP implementation against api.anthropic.com/v1/messages.
// Supports tool calling + SSE streaming via the native Anthropic format.
public sealed class AnthropicProvider : IStreamingToolCallingLlmProvider
{
    public string ProviderType => "anthropic";

    // Gate for dumping every SSE line at Debug level — mirrors the
    // OpenAiProvider switch so operators can opt into raw-chunk triage
    // without drowning steady-state logs. Env var: AI_LOG_PAYLOADS=true.
    private static readonly bool LogPayloads =
        string.Equals(Environment.GetEnvironmentVariable("AI_LOG_PAYLOADS"),
            "true", StringComparison.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly ILogger<AnthropicProvider> _logger;

    private readonly ModelLimits? _limits;
    private readonly string _messagesUrl;

    public AnthropicProvider(HttpClient http, string apiKey, string? baseUrl, ILogger<AnthropicProvider> logger,
        ModelLimits? limits = null)
    {
        _http = http;
        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? "https://api.anthropic.com").TrimEnd('/');
        _logger = logger;
        _limits = limits;
        // A base URL pasted with the version on it ("…/v1") would otherwise become
        // /v1/v1/messages. See LlmHttp.CombineUrl.
        _messagesUrl = LlmHttp.CombineUrl(_baseUrl, "/v1/messages");
    }

    public async Task<ChatResult> ChatAsync(
        List<LlmMessage> messages, string model, double temperature, CancellationToken ct)
        => await ChatWithToolsAsync(messages, new(), model, temperature, ct);

    public async Task<ChatResult> ChatWithToolsAsync(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature, CancellationToken ct)
    {
        var body = BuildBody(messages, tools, model, temperature, stream: false);
        using var response = await PostAsync(body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        var result = ParseResponse(doc.RootElement);
        _logger.LogDebug(
            "anthropic.chat.ok model={Model} input_tokens={InputTokens} output_tokens={OutputTokens}",
            model, result.InputTokens, result.OutputTokens);
        return result;
    }

    public async IAsyncEnumerable<ChatStreamEvent> ChatWithToolsStreamAsync(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildBody(messages, tools, model, temperature, stream: true);
        using var response = await PostAsync(body, ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? currentToolId = null, currentToolName = null;
        var toolArgs = new StringBuilder();
        var toolCallCount = 0;

        _logger.LogDebug(
            "anthropic.stream.connected model={Model} messages={MessageCount} tools={ToolCount}",
            model, messages.Count, tools.Count);

        // `message_delta` carries stop_reason ("end_turn", "tool_use", "max_tokens").
        string? stopReason = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (LogPayloads)
                _logger.LogDebug("anthropic.stream.raw_chunk {Data}", data.Length > 500 ? data[..500] + "…" : data);
            if (data == "[DONE]")
            {
                _logger.LogDebug("anthropic.stream.done tool_calls={ToolCalls}", toolCallCount);
                break;
            }

            JsonElement evt;
            try { evt = JsonDocument.Parse(data).RootElement; }
            catch { continue; }

            var type = evt.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "content_block_start":
                    if (evt.TryGetProperty("content_block", out var cb)
                        && cb.TryGetProperty("type", out var cbt)
                        && cbt.GetString() == "tool_use")
                    {
                        currentToolId = cb.TryGetProperty("id", out var id) ? id.GetString() : Guid.NewGuid().ToString();
                        currentToolName = cb.TryGetProperty("name", out var n) ? n.GetString() : "";
                        toolArgs.Clear();
                    }
                    break;

                case "content_block_delta":
                    if (evt.TryGetProperty("delta", out var delta))
                    {
                        var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
                        if (deltaType == "text_delta")
                        {
                            var text = delta.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                            if (text != null)
                                yield return new ChatStreamEvent { Type = "text_delta", TextDelta = text };
                        }
                        else if (deltaType == "input_json_delta")
                        {
                            var partial = delta.TryGetProperty("partial_json", out var pj) ? pj.GetString() : null;
                            if (partial != null) toolArgs.Append(partial);
                        }
                    }
                    break;

                case "content_block_stop":
                    if (currentToolId != null)
                    {
                        JsonElement argsJson;
                        try
                        {
                            argsJson = toolArgs.Length > 0
                                ? JsonDocument.Parse(toolArgs.ToString()).RootElement
                                : JsonDocument.Parse("{}").RootElement;
                        }
                        catch (JsonException ex)
                        {
                            // A tool_use block cut short by max_tokens: not a call the
                            // model finished making. Drop it and let stop_reason say why.
                            _logger.LogWarning(
                                "anthropic.stream.tool_call_unparseable name={Name} error={Error}",
                                currentToolName, ex.Message);
                            currentToolId = null;
                            currentToolName = null;
                            toolArgs.Clear();
                            break;
                        }
                        yield return new ChatStreamEvent
                        {
                            Type = "tool_call",
                            ToolCall = new ToolCallResult
                            {
                                Id = currentToolId!,
                                Name = currentToolName ?? "",
                                Arguments = argsJson,
                            },
                        };
                        currentToolId = null;
                        currentToolName = null;
                        toolArgs.Clear();
                        toolCallCount++;
                    }
                    break;

                case "message_delta":
                    if (evt.TryGetProperty("delta", out var md) && md.ValueKind == JsonValueKind.Object
                        && md.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                        stopReason = sr.GetString();
                    if (evt.TryGetProperty("usage", out var usage))
                    {
                        _logger.LogDebug("anthropic.stream.done tool_calls={ToolCalls} stop_reason={StopReason}", toolCallCount, stopReason);
                        yield return new ChatStreamEvent
                        {
                            Type = "done",
                            OutputTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0,
                            StopReason = stopReason,
                        };
                    }
                    break;
            }
        }
    }

    // Anthropic requires max_tokens on every request — there is no "as much as the
    // model will give" — so a number has to be chosen here. Non-streaming stays well
    // under the transport timeout; a stream has no such ceiling and gets room for a
    // long answer. Config.model_limits overrides both.
    //
    // This used to be a flat 8192 for every model and every call, which quietly cut
    // any long answer on a model that allows 64k or 128k.
    private const int MaxTokensNonStreaming = 16_000;
    private const int MaxTokensStreaming = 64_000;

    // A request above a model's own output cap is a 400, so the smaller of the two
    // wins — including when the number comes from Config, where a hand-typed value
    // can otherwise take the whole provider down.
    //
    // Opus 4 and 4.1 are the entries worth naming: they are not old enough to look
    // suspicious, they are the only 4.x models that stop at 32k, and at the streaming
    // default they would have failed every request outright.
    private static int CapFor(string model) =>
        model.StartsWith("claude-opus-4-0", StringComparison.OrdinalIgnoreCase) ? 32_000 :
        model.StartsWith("claude-opus-4-1", StringComparison.OrdinalIgnoreCase) ? 32_000 :
        model.StartsWith("claude-3-7", StringComparison.OrdinalIgnoreCase) ? 64_000 :
        model.StartsWith("claude-3-5", StringComparison.OrdinalIgnoreCase) ? 8_192 :
        model.StartsWith("claude-3", StringComparison.OrdinalIgnoreCase) ? 4_096 :
        int.MaxValue;

    private int MaxTokensFor(string model, bool stream)
    {
        var want = _limits?.MaxOutputTokens ?? (stream ? MaxTokensStreaming : MaxTokensNonStreaming);
        var capped = Math.Min(want, CapFor(model));
        if (capped < want)
            // The clamp keeps the request legal, but it also shortens every answer this
            // model can give and nothing downstream would say why: a reply cut at 8k
            // reads like a reply the model chose to end.
            _logger.LogWarning(
                "anthropic.max_tokens.clamped model={Model} requested={Requested} model_cap={Cap} "
                + "(answers from this model are limited to its own output ceiling)",
                model, want, capped);
        return capped;
    }

    // Sampling parameters were removed on the current model families (Opus 4.7 and
    // later, Sonnet 5, Fable): `temperature` there is a 400, not a warning — and this
    // provider used to send it on every request, so every call to a current Claude
    // model failed. This list is of the models that still ACCEPT it, so a model
    // released after this code — the case that keeps recurring — runs at the provider
    // default rather than failing outright.
    private static readonly string[] SamplingModelPrefixes =
    [
        "claude-3",
        "claude-opus-4-0", "claude-opus-4-1", "claude-opus-4-5", "claude-opus-4-6",
        "claude-sonnet-4-0", "claude-sonnet-4-5", "claude-sonnet-4-6",
        "claude-haiku-4-5",
    ];

    private static bool AcceptsTemperature(string model)
        => SamplingModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private string BuildBody(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature, bool stream)
    {
        var (system, mapped) = MapMessages(messages);

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = MaxTokensFor(model, stream),
            ["messages"] = mapped,
            ["stream"] = stream,
        };
        if (!string.IsNullOrWhiteSpace(system)) body["system"] = system;
        if (AcceptsTemperature(model)) body["temperature"] = temperature;
        if (tools.Count > 0)
            body["tools"] = tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = t.ParametersSchema,
            }).ToList();

        return JsonSerializer.Serialize(body);
    }

    // Maps the shared message list onto Anthropic's shape: the leading system
    // messages become the top-level `system` field, everything else becomes
    // user/assistant turns made of content blocks.
    //
    // Three things this has to get right, and the previous version got none of them.
    // Only the FIRST system message was used and the rest were silently dropped, so
    // an agent whose prompt arrives as several system messages lost all but one.
    // Roles must alternate, so blocks are appended to the open turn whenever the role
    // repeats — which is also what puts a parallel tool round's results into a single
    // user message, the shape the API requires; two tool results in a row used to be
    // sent as two consecutive user turns. And a system message that arrives
    // mid-conversation is rendered as user text rather than discarded: only some
    // models accept a system entry inside `messages`, and the operator picks the model.
    internal static (string? System, List<Dictionary<string, object?>> Messages) MapMessages(
        List<LlmMessage> messages)
    {
        var system = new StringBuilder();
        var mapped = new List<Dictionary<string, object?>>();
        var blocks = new List<Dictionary<string, object?>>();
        string? openRole = null;

        void Flush()
        {
            // A turn that ends up with no blocks (an assistant message that was only an
            // empty string) is dropped: the API rejects an empty content array.
            if (openRole is not null && blocks.Count > 0)
                mapped.Add(new Dictionary<string, object?> { ["role"] = openRole, ["content"] = blocks });
            blocks = [];
            openRole = null;
        }

        void Open(string role)
        {
            if (openRole == role) return;
            Flush();
            openRole = role;
        }

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "system" when mapped.Count == 0 && openRole is null:
                    if (!string.IsNullOrWhiteSpace(m.Content))
                    {
                        if (system.Length > 0) system.Append("\n\n");
                        system.Append(m.Content);
                    }
                    break;

                case "system": // mid-conversation — see the note above
                case "user":
                    if (string.IsNullOrEmpty(m.Content)) break;
                    Open("user");
                    blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = m.Content });
                    break;

                case "tool":
                    Open("user");
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = m.ToolCallId,
                        ["content"] = m.Content ?? string.Empty,
                    });
                    break;

                case "assistant":
                    Open("assistant");
                    if (!string.IsNullOrEmpty(m.Content))
                        blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = m.Content });
                    foreach (var tc in m.ToolCalls ?? [])
                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "tool_use",
                            ["id"] = tc.Id,
                            ["name"] = tc.Name,
                            ["input"] = tc.Arguments,
                        });
                    break;
            }
        }
        Flush();

        // The conversation has to open on a user turn. The runner always builds one,
        // but a windowed history could in principle start on an assistant turn, and
        // that is a 400 rather than a degraded answer.
        if (mapped.Count > 0 && (string?)mapped[0]["role"] == "assistant")
            mapped.Insert(0, new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = new List<Dictionary<string, object?>>
                {
                    new() { ["type"] = "text", ["text"] = "(continuing the earlier conversation)" },
                },
            });

        return (system.Length > 0 ? system.ToString() : null, mapped);
    }

    private Task<HttpResponseMessage> PostAsync(string body, CancellationToken ct)
        => LlmHttp.PostAsync(_http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _messagesUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            // Anthropic authenticates on its own header, not Authorization: Bearer.
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            return req;
        }, "anthropic", _logger, ct);

    private static ChatResult ParseResponse(JsonElement root)
    {
        var content = "";
        var toolCalls = new List<ToolCallResult>();
        if (root.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in blocks.EnumerateArray())
            {
                var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                if (blockType == "text")
                    content += block.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                else if (blockType == "tool_use")
                {
                    toolCalls.Add(new ToolCallResult
                    {
                        Id = block.TryGetProperty("id", out var id) ? id.GetString()! : "",
                        Name = block.TryGetProperty("name", out var n) ? n.GetString()! : "",
                        Arguments = block.TryGetProperty("input", out var inp) ? inp : default,
                    });
                }
            }
        }

        // Guard on ValueKind before every hop: TryGetProperty throws on an
        // Undefined element, so a response without `usage` would take the whole
        // call down instead of reporting zero tokens.
        var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        var inputTokens = usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("input_tokens", out var it) && it.ValueKind == JsonValueKind.Number
                ? it.GetInt32() : 0;
        var outputTokens = usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("output_tokens", out var ot2) && ot2.ValueKind == JsonValueKind.Number
                ? ot2.GetInt32() : 0;

        return new ChatResult
        {
            Content = content,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            StopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null,
        };
    }
}
