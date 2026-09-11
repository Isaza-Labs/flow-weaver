using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Services.Ai.Providers;

// Direct HTTP implementation against api.openai.com/v1/chat/completions.
// Also works with OpenAI-compatible endpoints (Azure OpenAI, vLLM, etc.)
// by swapping the base URL.
//
// It backs more than the "openai" provider type: DeepSeek, Kimi (Moonshot) and
// Gemini's compatibility endpoint speak this same wire format, and the "custom"
// type points it at whatever OpenAI-compatible endpoint an operator runs. They
// differ only in the two ctor tails - `providerType`, which is what the logs call
// this instance, and `chatPath`, because Gemini does not serve /v1/chat/completions.
public sealed class OpenAiProvider : IStreamingToolCallingLlmProvider
{
    public const string DefaultChatPath = "/v1/chat/completions";

    public string ProviderType => _providerType;

    // When true, every incoming SSE line is dumped at Debug level. Intended
    // for operator triage — gated by the AI_LOG_PAYLOADS env var so the
    // steady-state log stream stays manageable.
    private static readonly bool LogPayloads =
        string.Equals(Environment.GetEnvironmentVariable("AI_LOG_PAYLOADS"),
            "true", StringComparison.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly ILogger<OpenAiProvider>? _logger;

    // Null means "do not send an output cap": OpenAI then allows up to the model's
    // own maximum, which is the right default for it. Set from the provider row's
    // Config.model_limits (see ModelLimits).
    private readonly ModelLimits? _limits;

    private readonly string _providerType;
    private readonly string _chatUrl;

    // Set once an endpoint has rejected `stream_options`. Provider instances are
    // resolved per turn, so this only has to survive that turn's tool-calling rounds
    // - long enough that one 400 does not cost a doubled request every round.
    private bool _streamOptionsUnsupported;

    public OpenAiProvider(HttpClient http, string apiKey, string? baseUrl = null, ILogger<OpenAiProvider>? logger = null,
        ModelLimits? limits = null, string providerType = "openai", string chatPath = DefaultChatPath)
    {
        _http = http;
        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? "https://api.openai.com").TrimEnd('/');
        _logger = logger;
        _limits = limits;
        _providerType = providerType;
        // A base URL pasted with the version on it ("https://api.deepseek.com/v1",
        // which is what DeepSeek's own docs publish) would otherwise become
        // /v1/v1/chat/completions. See LlmHttp.CombineUrl.
        _chatUrl = LlmHttp.CombineUrl(_baseUrl, chatPath);
    }

    public async Task<ChatResult> ChatAsync(
        List<LlmMessage> messages, string model, double temperature, CancellationToken ct)
        => await ChatWithToolsAsync(messages, new(), model, temperature, ct);

    public async Task<ChatResult> ChatWithToolsAsync(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature, CancellationToken ct)
    {
        using var response = await PostChatAsync(messages, tools, model, temperature, stream: false, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        return ParseResponse(doc.RootElement);
    }

    public async IAsyncEnumerable<ChatStreamEvent> ChatWithToolsStreamAsync(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await PostChatAsync(messages, tools, model, temperature, stream: true, ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var toolCallsInProgress = new Dictionary<int, (string id, string name, StringBuilder args)>();
        // Last non-null finish_reason seen on a choice. OpenAI sends it once, on the
        // final content chunk; it is reported on every "done" event that follows.
        string? finishReason = null;

        _logger?.LogDebug(
            "{Tag}.stream.connected model={Model} messages={MessageCount} tools={ToolCount}",
            _providerType, model, messages.Count, tools.Count);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
            var data = line[6..];

            if (LogPayloads)
                _logger?.LogDebug("{Tag}.stream.raw_chunk {Data}",
                    _providerType, data.Length > 500 ? data[..500] + "…" : data);
            if (data == "[DONE]")
            {
                foreach (var (_, (id, name, args)) in toolCallsInProgress)
                {
                    JsonElement argsJson;
                    try
                    {
                        argsJson = args.Length > 0
                            ? JsonDocument.Parse(args.ToString()).RootElement
                            : JsonDocument.Parse("{}").RootElement;
                    }
                    catch (JsonException ex)
                    {
                        // Arguments that do not parse are a stream that ended mid-call —
                        // finish_reason "length" — not a tool the model meant to run with
                        // those bytes. Dropping it lets "done" carry the stop reason
                        // instead of the turn dying on a parse error nobody can trace.
                        _logger?.LogWarning(
                            "{Tag}.stream.tool_call_unparseable name={Name} finish_reason={FinishReason} error={Error}",
                            _providerType, name, finishReason, ex.Message);
                        continue;
                    }
                    yield return new ChatStreamEvent
                    {
                        Type = "tool_call",
                        ToolCall = new ToolCallResult { Id = id, Name = name, Arguments = argsJson },
                    };
                }
                yield return new ChatStreamEvent { Type = "done", StopReason = finishReason };
                break;
            }

            JsonElement chunk;
            try { chunk = JsonDocument.Parse(data).RootElement; }
            catch { continue; }

            if (!chunk.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                // Intermediate usage-only chunk — OpenAI sends one of these
                // right before [DONE] when `stream_options.include_usage=true`.
                if (chunk.TryGetProperty("usage", out var u0) && u0.ValueKind == JsonValueKind.Object)
                {
                    yield return new ChatStreamEvent
                    {
                        Type = "done",
                        InputTokens = u0.TryGetProperty("prompt_tokens", out var pt0) && pt0.ValueKind == JsonValueKind.Number ? pt0.GetInt32() : 0,
                        OutputTokens = u0.TryGetProperty("completion_tokens", out var cpt0) && cpt0.ValueKind == JsonValueKind.Number ? cpt0.GetInt32() : 0,
                        StopReason = finishReason,
                    };
                }
                continue;
            }

            // `delta` must be an object for TryGetProperty to be safe. If it
            // is missing or null (happens in the final `finish_reason` chunk
            // and in some tool-only chunks), skip the per-delta branches.
            if (choices[0].TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
                finishReason = frEl.GetString();

            var delta = choices[0].TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object
                ? d
                : (JsonElement?)null;

            if (delta is { } deltaVal)
            {
                if (deltaVal.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String)
                {
                    var text = contentEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                        yield return new ChatStreamEvent { Type = "text_delta", TextDelta = text };
                }

                if (deltaVal.TryGetProperty("tool_calls", out var tcs)
                    && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        if (tc.ValueKind != JsonValueKind.Object) continue;
                        var idx = tc.TryGetProperty("index", out var ie) && ie.ValueKind == JsonValueKind.Number
                            ? ie.GetInt32() : 0;
                        if (tc.TryGetProperty("id", out var tcId) && tcId.ValueKind == JsonValueKind.String)
                        {
                            var name = tc.TryGetProperty("function", out var fn)
                                && fn.ValueKind == JsonValueKind.Object
                                && fn.TryGetProperty("name", out var fnName)
                                && fnName.ValueKind == JsonValueKind.String
                                    ? fnName.GetString() ?? ""
                                    : "";
                            toolCallsInProgress[idx] = (tcId.GetString()!, name, new StringBuilder());
                        }
                        if (tc.TryGetProperty("function", out var fnDelta)
                            && fnDelta.ValueKind == JsonValueKind.Object
                            && fnDelta.TryGetProperty("arguments", out var argsD)
                            && argsD.ValueKind == JsonValueKind.String
                            && toolCallsInProgress.TryGetValue(idx, out var prog))
                        {
                            prog.args.Append(argsD.GetString());
                        }
                    }
                }
            }

            // Usage frequently arrives as JSON `null` until the very last
            // chunk, so filter on ValueKind before diving in.
            if (chunk.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                yield return new ChatStreamEvent
                {
                    Type = "done",
                    InputTokens = usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : 0,
                    OutputTokens = usage.TryGetProperty("completion_tokens", out var cpt) && cpt.ValueKind == JsonValueKind.Number ? cpt.GetInt32() : 0,
                    StopReason = finishReason,
                };
            }
        }
    }

    private string BuildBody(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature, bool stream, bool includeUsage = true)
    {
        var mapped = messages.Select(m =>
        {
            if (m.Role == "tool")
                return new Dictionary<string, object?> { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Content };
            if (m.ToolCalls is { Count: > 0 })
            {
                return new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = m.Content,
                    ["tool_calls"] = m.ToolCalls.Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new { name = tc.Name, arguments = tc.Arguments.GetRawText() },
                    }).ToList(),
                };
            }
            return new Dictionary<string, object?> { ["role"] = m.Role, ["content"] = m.Content };
        }).ToList();

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = mapped,
            ["stream"] = stream,
        };
        // GPT-5 and the o-series reasoning models reject any temperature
        // other than the default (1) with a 400. BuildBody used to send it
        // unconditionally, which fails the whole request. Only include the
        // field for models that honor it — omitting it lets the others fall
        // back to their required default instead of erroring.
        if (SupportsCustomTemperature(model))
            body["temperature"] = temperature;
        // The same family that rejects temperature also rejects `max_tokens` and
        // wants `max_completion_tokens`; older models and most OpenAI-compatible
        // gateways only know `max_tokens`. Sent only when configured.
        if (_limits is not null)
            body[SupportsCustomTemperature(model) ? "max_tokens" : "max_completion_tokens"] = _limits.MaxOutputTokens;
        if (stream && includeUsage) body["stream_options"] = new { include_usage = true };
        if (tools.Count > 0)
            body["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema },
            }).ToList();

        return JsonSerializer.Serialize(body);
    }

    // GPT-5 (gpt-5, gpt-5-mini, gpt-5.4, …) and the o-series reasoning
    // models (o1/o3/o4) only accept the default temperature of 1 and reject
    // any other value with a 400. The "gpt-5-chat" variant does honor a
    // custom temperature, but omitting it there is harmless (same default),
    // so we treat the family uniformly for safety.
    private static bool SupportsCustomTemperature(string model)
    {
        var m = model.Trim();
        return !(m.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("o4", StringComparison.OrdinalIgnoreCase));
    }

    // `stream_options.include_usage` is how the token counts arrive on a stream, and
    // OpenAI, DeepSeek and Kimi all honour it. Not every OpenAI-compatible endpoint
    // does: an older vLLM, a llama.cpp server or a strict gateway - the `custom` type
    // - answers 400, which is not retryable, so the whole provider would be unusable
    // over a field that only buys accounting. So it is asked for once and dropped for
    // the rest of the turn if the endpoint says no; the turn then runs with usage
    // reported as 0 rather than not running at all.
    private async Task<HttpResponseMessage> PostChatAsync(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature, bool stream, CancellationToken ct)
    {
        var includeUsage = stream && !_streamOptionsUnsupported;
        var body = BuildBody(messages, tools, model, temperature, stream, includeUsage);
        if (!includeUsage) return await PostAsync(body, ct);

        try
        {
            return await PostAsync(body, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // A 400 has other causes (an unknown model, a malformed tool schema), and
            // the retry below returns the same error for those - one extra request,
            // and this line says which hypothesis was tested.
            _logger?.LogWarning(
                "{Tag}.post.retry_without_stream_options error={Error}", _providerType, ex.Message);
            var response = await PostAsync(
                BuildBody(messages, tools, model, temperature, stream, includeUsage: false), ct);
            _streamOptionsUnsupported = true;
            return response;
        }
    }

    private Task<HttpResponseMessage> PostAsync(string body, CancellationToken ct)
        => LlmHttp.PostAsync(_http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _chatUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            return req;
        }, _providerType, _logger, ct);

    private static ChatResult ParseResponse(JsonElement root)
    {
        // Every hop guards on ValueKind: TryGetProperty THROWS on an Undefined
        // element, so a response missing `choices`, `message` or `usage` (empty
        // choices after a content filter, OpenAI-compatible gateways that omit
        // usage) used to blow up the whole call instead of degrading to empties.
        var choice = root.TryGetProperty("choices", out var cs)
            && cs.ValueKind == JsonValueKind.Array && cs.GetArrayLength() > 0
                ? cs[0]
                : default;
        var msg = choice.ValueKind == JsonValueKind.Object
            && choice.TryGetProperty("message", out var m) ? m : default;
        var content = msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? ""
                : "";

        var toolCalls = new List<ToolCallResult>();
        if (msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcs.EnumerateArray())
            {
                var id = tc.TryGetProperty("id", out var tid) ? tid.GetString()! : "";
                var fn = tc.TryGetProperty("function", out var f) ? f : default;
                var name = fn.TryGetProperty("name", out var n) ? n.GetString()! : "";
                var argsStr = fn.TryGetProperty("arguments", out var a) ? a.GetString() ?? "{}" : "{}";
                toolCalls.Add(new ToolCallResult
                {
                    Id = id,
                    Name = name,
                    Arguments = JsonDocument.Parse(argsStr).RootElement,
                });
            }
        }

        var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        var inputTokens = usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("prompt_tokens", out var it) && it.ValueKind == JsonValueKind.Number
                ? it.GetInt32() : 0;
        var outputTokens = usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("completion_tokens", out var ot) && ot.ValueKind == JsonValueKind.Number
                ? ot.GetInt32() : 0;

        return new ChatResult
        {
            Content = content,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            StopReason = choice.ValueKind == JsonValueKind.Object
                && choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
                    ? fr.GetString() : null,
        };
    }
}
