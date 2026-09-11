using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Services.Ai.Providers;

// Raw HttpClient against Ollama's /api/chat endpoint (default localhost:11434).
// Tool calling depends on the model (llama3.1+ supports it natively).
public sealed class OllamaProvider : IStreamingToolCallingLlmProvider
{
    public string ProviderType => "ollama";

    // Gate for dumping every streamed JSON chunk at Debug level. Off
    // by default; flip AI_LOG_PAYLOADS=true to enable operator triage.
    private static readonly bool LogPayloads =
        string.Equals(Environment.GetEnvironmentVariable("AI_LOG_PAYLOADS"),
            "true", StringComparison.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger<OllamaProvider> _logger;

    // Ollama runs a model at its default context window (2k–4k for most) unless
    // num_ctx says otherwise, and a prompt that does not fit loses its beginning —
    // the system prompt — without a word. Config.model_limits is how a deployment
    // says how big the window really is.
    private readonly ModelLimits? _limits;

    // Sending nothing when nothing is configured was not neutral: it left the
    // silent truncation above in place for every unconfigured provider. 32k fits a
    // real prompt (the system prompt alone runs to tens of thousands of tokens)
    // plus a few tool rounds, and still loads on a 12–16 GB GPU for a quantised
    // 7–8B model. A deployment with less VRAM, or a model whose real context is
    // shorter, sets model_limits.context_window instead.
    public const int DefaultNumCtx = 32_768;

    private readonly string _chatUrl;
    private readonly string? _apiKey;

    public OllamaProvider(HttpClient http, string? baseUrl, ILogger<OllamaProvider> logger,
        ModelLimits? limits = null, string? apiKey = null)
    {
        _http = http;
        _baseUrl = (baseUrl ?? "http://localhost:11434").TrimEnd('/');
        _logger = logger;
        _limits = limits;
        _apiKey = apiKey;
        // "http://host/api" is a natural thing to paste; it must not become
        // /api/api/chat. See LlmHttp.CombineUrl.
        _chatUrl = LlmHttp.CombineUrl(_baseUrl, "/api/chat");
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
            "ollama.chat.ok model={Model} input_tokens={InputTokens} output_tokens={OutputTokens}",
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

        _logger.LogDebug(
            "ollama.stream.connected model={Model} messages={MessageCount} tools={ToolCount}",
            model, messages.Count, tools.Count);

        var contentBuf = new StringBuilder();
        var toolCallCount = 0;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrEmpty(line)) continue;

            if (LogPayloads)
                _logger.LogDebug("ollama.stream.raw_chunk {Data}", line.Length > 500 ? line[..500] + "…" : line);

            JsonElement chunk;
            try
            {
                chunk = JsonDocument.Parse(line).RootElement;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("ollama.stream.parse_error error={Error}", ex.Message);
                continue;
            }

            var done = chunk.TryGetProperty("done", out var d)
                && d.ValueKind is JsonValueKind.True or JsonValueKind.False && d.GetBoolean();
            if (chunk.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                var content = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null;
                if (!string.IsNullOrEmpty(content))
                    yield return new ChatStreamEvent { Type = "text_delta", TextDelta = content };

                if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        if (tc.ValueKind != JsonValueKind.Object) continue;
                        var fn = tc.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object
                            ? f : default;
                        yield return new ChatStreamEvent
                        {
                            Type = "tool_call",
                            ToolCall = new ToolCallResult
                            {
                                Id = Guid.NewGuid().ToString("N")[..12],
                                Name = fn.ValueKind == JsonValueKind.Object
                                    && fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                                        ? n.GetString()! : "",
                                Arguments = fn.ValueKind == JsonValueKind.Object
                                    && fn.TryGetProperty("arguments", out var a) ? a : default,
                            },
                        };
                        toolCallCount++;
                    }
                }
            }

            if (done)
            {
                _logger.LogDebug("ollama.stream.done tool_calls={ToolCalls}", toolCallCount);
                yield return new ChatStreamEvent
                {
                    Type = "done",
                    InputTokens = chunk.TryGetProperty("prompt_eval_count", out var pe) && pe.ValueKind == JsonValueKind.Number
                        ? pe.GetInt32() : 0,
                    OutputTokens = chunk.TryGetProperty("eval_count", out var ec) && ec.ValueKind == JsonValueKind.Number
                        ? ec.GetInt32() : 0,
                    // "stop" or "length" — the latter is num_predict running out.
                    StopReason = chunk.TryGetProperty("done_reason", out var dr) && dr.ValueKind == JsonValueKind.String
                        ? dr.GetString() : null,
                };
                break;
            }
        }
    }

    private string BuildBody(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature, bool stream)
    {
        var mapped = messages.Select(m =>
        {
            var obj = new Dictionary<string, object?> { ["role"] = m.Role, ["content"] = m.Content ?? "" };
            if (m.ToolCalls is { Count: > 0 })
            {
                obj["tool_calls"] = m.ToolCalls.Select(tc => new
                {
                    function = new { name = tc.Name, arguments = tc.Arguments },
                }).ToList();
            }
            return obj;
        }).ToList();

        var options = new Dictionary<string, object?>
        {
            ["temperature"] = temperature,
            ["num_ctx"] = _limits?.ContextWindowTokens ?? DefaultNumCtx,
        };
        if (_limits is not null) options["num_predict"] = _limits.MaxOutputTokens;
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = mapped,
            ["stream"] = stream,
            ["options"] = options,
        };
        if (tools.Count > 0)
            body["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema },
            }).ToList();

        return JsonSerializer.Serialize(body);
    }

    private Task<HttpResponseMessage> PostAsync(string body, CancellationToken ct)
        => LlmHttp.PostAsync(_http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _chatUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            // A local Ollama needs no credential; the key is sent only when one is
            // configured, for the proxies people put in front of a shared instance.
            if (!string.IsNullOrEmpty(_apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            return req;
        }, "ollama", _logger, ct);

    private static ChatResult ParseResponse(JsonElement root)
    {
        // ValueKind guards throughout: TryGetProperty throws on an Undefined
        // element, so a chunk without `message` (or a tool call without
        // `function`) would take the request down instead of parsing to empty.
        var msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
            ? m : default;
        var content = msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";

        var toolCalls = new List<ToolCallResult>();
        if (msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcs.EnumerateArray())
            {
                if (tc.ValueKind != JsonValueKind.Object) continue;
                var fn = tc.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object
                    ? f : default;
                toolCalls.Add(new ToolCallResult
                {
                    Id = Guid.NewGuid().ToString("N")[..12],
                    Name = fn.ValueKind == JsonValueKind.Object
                        && fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                            ? n.GetString()! : "",
                    Arguments = fn.ValueKind == JsonValueKind.Object
                        && fn.TryGetProperty("arguments", out var a) ? a : default,
                });
            }
        }

        return new ChatResult
        {
            Content = content,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            InputTokens = root.TryGetProperty("prompt_eval_count", out var pe) && pe.ValueKind == JsonValueKind.Number
                ? pe.GetInt32() : 0,
            OutputTokens = root.TryGetProperty("eval_count", out var ec) && ec.ValueKind == JsonValueKind.Number
                ? ec.GetInt32() : 0,
            StopReason = root.TryGetProperty("done_reason", out var dr) && dr.ValueKind == JsonValueKind.String
                ? dr.GetString() : null,
        };
    }
}
