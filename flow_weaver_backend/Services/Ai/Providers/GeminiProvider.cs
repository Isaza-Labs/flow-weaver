using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Services.Ai.Providers;

// Direct HTTP implementation against the Google Gemini API
// (generativelanguage.googleapis.com/v1beta/models/{model}:generateContent).
// The wire format differs from both OpenAI and Anthropic on three points that
// matter here: turns are called `contents` and the assistant role is "model",
// the system prompt lives in `systemInstruction`, and tool results are matched
// by function NAME rather than by a call id — Gemini never mints one.
public sealed class GeminiProvider : IStreamingToolCallingLlmProvider
{
    public string ProviderType => "gemini";

    // Gate for dumping every SSE line at Debug level — mirrors the
    // OpenAiProvider switch so operators can opt into raw-chunk triage
    // without drowning steady-state logs. Env var: AI_LOG_PAYLOADS=true.
    private static readonly bool LogPayloads =
        string.Equals(Environment.GetEnvironmentVariable("AI_LOG_PAYLOADS"),
            "true", StringComparison.OrdinalIgnoreCase);

    // Sent as generationConfig.maxOutputTokens. 8192 is accepted by every Gemini
    // 1.5+ model; it was 4096, which cut long answers silently. Override per
    // provider with Config.model_limits.
    public const int DefaultMaxOutputTokens = 8192;

    private readonly ModelLimits? _limits;

    private static readonly JsonElement EmptyArgs =
        JsonDocument.Parse("{}").RootElement.Clone();

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly ILogger<GeminiProvider> _logger;

    public GeminiProvider(HttpClient http, string apiKey, string? baseUrl, ILogger<GeminiProvider> logger,
        ModelLimits? limits = null)
    {
        _http = http;
        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? "https://generativelanguage.googleapis.com").TrimEnd('/');
        _logger = logger;
        _limits = limits;
    }

    public async Task<ChatResult> ChatAsync(
        List<LlmMessage> messages, string model, double temperature, CancellationToken ct)
        => await ChatWithToolsAsync(messages, new(), model, temperature, ct);

    public async Task<ChatResult> ChatWithToolsAsync(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature, CancellationToken ct)
    {
        var body = BuildBody(messages, tools, temperature);
        using var response = await PostAsync(model, body, stream: false, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json);
        var result = ParseResponse(doc.RootElement);
        _logger.LogDebug(
            "gemini.chat.ok model={Model} input_tokens={InputTokens} output_tokens={OutputTokens}",
            model, result.InputTokens, result.OutputTokens);
        return result;
    }

    public async IAsyncEnumerable<ChatStreamEvent> ChatWithToolsStreamAsync(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildBody(messages, tools, temperature);
        using var response = await PostAsync(model, body, stream: true, ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var toolCallCount = 0;
        int inputTokens = 0, outputTokens = 0;
        // finishReason rides on the candidate of the last chunk ("STOP",
        // "MAX_TOKENS", ...). Kept so the synthesised `done` can carry it.
        string? finishReason = null;

        _logger.LogDebug(
            "gemini.stream.connected model={Model} messages={MessageCount} tools={ToolCount}",
            model, messages.Count, tools.Count);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (LogPayloads)
                _logger.LogDebug("gemini.stream.raw_chunk {Data}", data.Length > 500 ? data[..500] + "…" : data);
            if (data == "[DONE]") break;

            JsonElement chunk;
            try { chunk = JsonDocument.Parse(data).RootElement; }
            catch (JsonException) { continue; }

            var candidate = FirstCandidate(chunk);
            if (candidate.ValueKind == JsonValueKind.Object
                && candidate.TryGetProperty("finishReason", out var frEl)
                && frEl.ValueKind == JsonValueKind.String)
                finishReason = frEl.GetString();

            foreach (var part in Parts(candidate))
            {
                // Thought summaries ride in the same parts array as the answer
                // when a 2.5 model is asked for them; they are not reply text.
                if (part.TryGetProperty("thought", out var th)
                    && th.ValueKind == JsonValueKind.True) continue;

                if (part.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                {
                    var text = tx.GetString();
                    if (!string.IsNullOrEmpty(text))
                        yield return new ChatStreamEvent { Type = "text_delta", TextDelta = text };
                }
                else if (part.TryGetProperty("functionCall", out var fc)
                    && fc.ValueKind == JsonValueKind.Object)
                {
                    // Unlike OpenAI, Gemini never splits a functionCall across
                    // chunks — arguments arrive whole, so there is nothing to
                    // accumulate.
                    yield return new ChatStreamEvent { Type = "tool_call", ToolCall = ToToolCall(fc) };
                    toolCallCount++;
                }
            }

            // usageMetadata repeats on every chunk with running totals; keep
            // the latest so the synthesised `done` carries the final counts.
            if (chunk.TryGetProperty("usageMetadata", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = ReadInt(usage, "promptTokenCount", inputTokens);
                outputTokens = ReadInt(usage, "candidatesTokenCount", outputTokens);
            }
        }

        // Gemini terminates the SSE body without a sentinel event (no [DONE],
        // no `finish` frame of its own), so `done` is synthesised on close.
        _logger.LogDebug("gemini.stream.done tool_calls={ToolCalls} finish_reason={FinishReason}", toolCallCount, finishReason);
        yield return new ChatStreamEvent
        {
            Type = "done",
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            StopReason = finishReason,
        };
    }

    // ─── request shaping ────────────────────────────────────────────────

    private string BuildBody(List<LlmMessage> messages, List<ToolDefinition> tools, double temperature)
    {
        var system = string.Join("\n\n", messages
            .Where(m => m.Role == "system" && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content));

        // A Gemini functionResponse is keyed by the function NAME, not by a call
        // id, so the pairing has to be recovered from the assistant turns
        // already in the history.
        var namesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            if (m.ToolCalls is not { Count: > 0 }) continue;
            foreach (var tc in m.ToolCalls) namesByCallId[tc.Id] = tc.Name;
        }

        var contents = new List<Dictionary<string, object?>>();
        foreach (var msg in messages)
        {
            if (msg.Role == "system") continue;
            var (role, parts) = MapMessage(msg, namesByCallId);
            if (parts.Count == 0) continue;

            // Consecutive same-role turns are merged: parallel tool calls come
            // back as one "tool" message each, and Gemini expects all of their
            // responses inside a single content.
            if (contents.Count > 0 && (string?)contents[^1]["role"] == role)
                ((List<object>)contents[^1]["parts"]!).AddRange(parts);
            else
                contents.Add(new() { ["role"] = role, ["parts"] = parts });
        }

        var body = new Dictionary<string, object?>
        {
            ["contents"] = contents,
            ["generationConfig"] = new Dictionary<string, object?>
            {
                ["temperature"] = temperature,
                ["maxOutputTokens"] = _limits?.MaxOutputTokens ?? DefaultMaxOutputTokens,
            },
        };
        if (!string.IsNullOrEmpty(system))
            body["systemInstruction"] = new { parts = new[] { new { text = system } } };

        if (tools.Count > 0)
        {
            var declarations = tools.Select(t =>
            {
                var decl = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                };
                var schema = SanitizeSchema(t.ParametersSchema);
                // An OBJECT schema with no properties is rejected; omitting
                // `parameters` is how Gemini spells "takes no arguments".
                if (schema is not null && schema.ContainsKey("properties"))
                    decl["parameters"] = schema;
                return decl;
            }).ToList();

            body["tools"] = new[]
            {
                new Dictionary<string, object?> { ["functionDeclarations"] = declarations },
            };
        }

        return JsonSerializer.Serialize(body);
    }

    private static (string Role, List<object> Parts) MapMessage(
        LlmMessage msg, IReadOnlyDictionary<string, string> namesByCallId)
    {
        var parts = new List<object>();

        if (msg.Role == "tool")
        {
            parts.Add(new
            {
                functionResponse = new
                {
                    name = ResolveFunctionName(msg.ToolCallId, namesByCallId),
                    response = ToResponseObject(msg.Content),
                },
            });
            // Gemini has no "tool" role — function results ride in a user turn.
            return ("user", parts);
        }

        if (!string.IsNullOrEmpty(msg.Content))
            parts.Add(new { text = msg.Content });

        if (msg.ToolCalls is { Count: > 0 })
        {
            foreach (var tc in msg.ToolCalls)
                parts.Add(new { functionCall = new { name = tc.Name, args = ArgsOrEmpty(tc.Arguments) } });
        }

        return (msg.Role == "assistant" ? "model" : "user", parts);
    }

    // Ids we mint look like "<name>:<8 hex>" precisely so the function name
    // survives a history that no longer carries the assistant turn.
    private static string ResolveFunctionName(
        string? toolCallId, IReadOnlyDictionary<string, string> namesByCallId)
    {
        if (string.IsNullOrEmpty(toolCallId)) return string.Empty;
        if (namesByCallId.TryGetValue(toolCallId, out var name)) return name;
        var sep = toolCallId.LastIndexOf(':');
        return sep > 0 ? toolCallId[..sep] : toolCallId;
    }

    private static object ArgsOrEmpty(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.Object
            ? arguments
            : new Dictionary<string, object?>();

    // functionResponse.response must be a JSON object. Tool handlers return
    // arbitrary JSON (often an array) or plain text, so anything that isn't an
    // object gets wrapped under `result`.
    private static object ToResponseObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return new Dictionary<string, object?>();
        try
        {
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.Clone()
                : new Dictionary<string, object?> { ["result"] = doc.RootElement.Clone() };
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?> { ["result"] = content };
        }
    }

    // ─── schema sanitising ──────────────────────────────────────────────

    // Gemini validates function parameters against its own OpenAPI subset and
    // rejects the whole request with a 400 on any unknown keyword. Our tool
    // schemas are written for OpenAI's stricter draft (they carry
    // `additionalProperties`, `$schema`, `const`, …), so unsupported keys are
    // dropped rather than forwarded.
    private static readonly HashSet<string> SupportedSchemaKeys = new(StringComparer.Ordinal)
    {
        "type", "format", "title", "description", "nullable", "enum", "items",
        "properties", "required", "minItems", "maxItems", "minProperties",
        "maxProperties", "minLength", "maxLength", "pattern", "minimum",
        "maximum", "default", "anyOf", "example", "propertyOrdering",
    };

    // `format` is likewise an enumeration, not free text — an unknown value is
    // a 400 even though the keyword itself is supported.
    private static readonly HashSet<string> SupportedFormats = new(StringComparer.Ordinal)
    {
        "int32", "int64", "float", "double", "date-time", "enum",
    };

    private static Dictionary<string, object?>? SanitizeSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return null;

        var result = new Dictionary<string, object?>();
        foreach (var prop in schema.EnumerateObject())
        {
            if (!SupportedSchemaKeys.Contains(prop.Name)) continue;

            switch (prop.Name)
            {
                case "type":
                    var (type, nullable) = NormalizeType(prop.Value);
                    if (type is not null) result["type"] = type;
                    if (nullable) result["nullable"] = true;
                    break;

                case "format":
                    if (prop.Value.ValueKind == JsonValueKind.String
                        && SupportedFormats.Contains(prop.Value.GetString()!))
                        result["format"] = prop.Value.GetString();
                    break;

                case "properties":
                    if (prop.Value.ValueKind != JsonValueKind.Object) break;
                    var props = new Dictionary<string, object?>();
                    foreach (var child in prop.Value.EnumerateObject())
                    {
                        var sanitized = SanitizeSchema(child.Value);
                        if (sanitized is not null) props[child.Name] = sanitized;
                    }
                    if (props.Count > 0) result["properties"] = props;
                    break;

                case "items":
                    var items = SanitizeSchema(prop.Value);
                    if (items is not null) result["items"] = items;
                    break;

                case "anyOf":
                    if (prop.Value.ValueKind != JsonValueKind.Array) break;
                    var branches = prop.Value.EnumerateArray()
                        .Select(SanitizeSchema)
                        .Where(b => b is not null)
                        .ToList();
                    if (branches.Count > 0) result["anyOf"] = branches;
                    break;

                default:
                    // Scalars and plain arrays (enum, required, minimum, …) pass
                    // through verbatim — JsonElement serialises as raw JSON.
                    result[prop.Name] = prop.Value;
                    break;
            }
        }
        return result;
    }

    // Gemini's `type` is a proto enum, so the canonical upper-case name is the
    // safest spelling. A union such as ["string","null"] has no equivalent and
    // collapses to the concrete type plus `nullable`.
    private static (string? Type, bool Nullable) NormalizeType(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var t = value.GetString();
            return string.Equals(t, "null", StringComparison.OrdinalIgnoreCase)
                ? (null, true)
                : (t?.ToUpperInvariant(), false);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            string? concrete = null;
            var nullable = false;
            foreach (var el in value.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                var t = el.GetString();
                if (string.Equals(t, "null", StringComparison.OrdinalIgnoreCase)) { nullable = true; continue; }
                concrete ??= t?.ToUpperInvariant();
            }
            return (concrete, nullable);
        }
        return (null, false);
    }

    // ─── transport ──────────────────────────────────────────────────────

    // The model id is part of the path, so a value copied from the docs with
    // the collection prefix ("models/gemini-2.5-pro") must not double it up.
    private static string NormalizeModel(string model)
    {
        var m = model.Trim();
        return m.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? m["models/".Length..]
            : m;
    }

    // Google publishes the base URL both bare and with /v1beta already on it, so
    // CombineUrl decides whether that segment still needs adding instead of
    // assuming it does and producing /v1beta/v1beta/models/...
    private string Endpoint(string model, bool stream)
        => LlmHttp.CombineUrl(_baseUrl, stream
            ? $"/v1beta/models/{NormalizeModel(model)}:streamGenerateContent?alt=sse"
            : $"/v1beta/models/{NormalizeModel(model)}:generateContent");

    // Retry only these upstream transient codes. Gemini answers 503 UNAVAILABLE
    // ("this model is currently experiencing high demand") and 429
    // RESOURCE_EXHAUSTED often enough on the popular models that a single one
    // would otherwise kill a whole agent turn. Anything else (400 rejected
    // schema, 401 bad key, 404 unknown model) is fatal on the first try —
    // retrying just burns latency and repeats the same error.
    private static readonly HashSet<int> RetryableStatusCodes = new() { 408, 429, 500, 502, 503, 504 };

    // Backoff schedule: 0.5s, 1s, 2s — worst case ~3.5s of waiting on top of the
    // request latency. Matches OpenAiProvider so both chat paths behave the same
    // under a brief upstream outage.
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    };

    private async Task<HttpResponseMessage> PostAsync(
        string model, string body, bool stream, CancellationToken ct)
    {
        // Retrying is safe on the streaming path too: this runs before the body
        // is consumed and before the iterator has yielded anything, so a retried
        // attempt cannot duplicate text the caller already saw.
        HttpResponseMessage? response = null;
        for (var attempt = 0; ; attempt++)
        {
            response?.Dispose();

            // Each attempt needs its own HttpRequestMessage — HttpClient
            // disallows resending a message whose Content stream was consumed.
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint(model, stream));
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            // Header auth rather than ?key= so the secret never lands in a proxy
            // access log alongside the URL.
            req.Headers.Add("x-goog-api-key", _apiKey);

            try
            {
                response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (attempt < RetryDelays.Length && !ct.IsCancellationRequested)
            {
                // Network-level failure (DNS, socket reset, TLS). Retryable for
                // the same reason as a 503: no status code was observed, so the
                // request may simply not have landed.
                _logger.LogWarning(
                    "gemini.post.network_retry attempt={Attempt} error={Error}", attempt + 1, ex.Message);
                await DelayWithJitterAsync(RetryDelays[attempt], ct);
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "gemini.request.failed reason=network");
                throw;
            }

            if (response.IsSuccessStatusCode) return response;

            var status = (int)response.StatusCode;
            var errorBody = await SafeReadBodyAsync(response, ct);

            if (!RetryableStatusCodes.Contains(status) || attempt >= RetryDelays.Length)
            {
                _logger.LogError(
                    "gemini.request.failed status={Status} body={Body}", status, errorBody);

                // EnsureSuccessStatusCode would throw "Response status code does
                // not indicate success: 404" and discard the only actionable
                // part. Google puts the real reason in `error.message` (wrong
                // model id, rejected parameter schema, quota), and that has to
                // reach the chat surface and the provider Test button — not just
                // the log.
                var statusCode = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException(
                    $"gemini {status}: {ExtractErrorMessage(errorBody)}", null, statusCode);
            }

            // Honor Retry-After when Google sends one, capped to our own maximum
            // so a misconfigured header can't pin the turn for minutes.
            var delay = RetryDelays[attempt];
            if (response.Headers.RetryAfter?.Delta is { } ra && ra > TimeSpan.Zero)
                delay = ra > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : ra;

            _logger.LogWarning(
                "gemini.post.retry attempt={Attempt} status={Status} delay_ms={Delay}",
                attempt + 1, status, (int)delay.TotalMilliseconds);
            await DelayWithJitterAsync(delay, ct);
        }
    }

    // ±40% jitter around the base delay so simultaneous retries from different
    // conversations stagger instead of hammering the API the same millisecond
    // after a recovery.
    private static Task DelayWithJitterAsync(TimeSpan baseDelay, CancellationToken ct)
    {
        var jitterMs = (int)(baseDelay.TotalMilliseconds * 0.4);
        var offset = Random.Shared.Next(-jitterMs, jitterMs + 1);
        var final = baseDelay + TimeSpan.FromMilliseconds(offset);
        if (final < TimeSpan.Zero) final = TimeSpan.Zero;
        return Task.Delay(final, ct);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(body) ? "(empty)" : body;
        }
        catch (Exception ex)
        {
            return $"(failed to read error body: {ex.Message})";
        }
    }

    // Gemini errors are shaped {"error":{"code":404,"message":"…","status":"…"}}.
    // Anything else — an HTML page from an interposed proxy, an empty body — falls
    // back to the raw text so the caller still gets something to act on.
    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return TrimForMessage(text!);
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the raw body.
        }
        return TrimForMessage(body);
    }

    // The Test endpoint already truncates at 400 chars; capping here keeps a
    // multi-megabyte proxy error page out of the exception message and the trace.
    private static string TrimForMessage(string s)
        => s.Length <= 400 ? s.Trim() : s[..400].Trim() + "…";

    // ─── response parsing ───────────────────────────────────────────────

    private static ChatResult ParseResponse(JsonElement root)
    {
        var candidate = FirstCandidate(root);

        var content = "";
        var toolCalls = new List<ToolCallResult>();
        foreach (var part in Parts(candidate))
        {
            if (part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True)
                continue;
            if (part.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                content += tx.GetString();
            else if (part.TryGetProperty("functionCall", out var fc) && fc.ValueKind == JsonValueKind.Object)
                toolCalls.Add(ToToolCall(fc));
        }

        // Guard on ValueKind before every hop: TryGetProperty throws on an
        // Undefined element, so a response without `usageMetadata` would take
        // the whole call down instead of reporting zero tokens.
        var usage = root.TryGetProperty("usageMetadata", out var u) && u.ValueKind == JsonValueKind.Object
            ? u : default;

        return new ChatResult
        {
            Content = content,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            InputTokens = ReadInt(usage, "promptTokenCount", 0),
            OutputTokens = ReadInt(usage, "candidatesTokenCount", 0),
            StopReason = candidate.ValueKind == JsonValueKind.Object
                && candidate.TryGetProperty("finishReason", out var fr)
                && fr.ValueKind == JsonValueKind.String
                    ? fr.GetString() : null,
        };
    }

    private static JsonElement FirstCandidate(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("candidates", out var cs)
            && cs.ValueKind == JsonValueKind.Array
            && cs.GetArrayLength() > 0
                ? cs[0]
                : default;

    private static IEnumerable<JsonElement> Parts(JsonElement candidate)
    {
        if (candidate.ValueKind != JsonValueKind.Object) yield break;
        if (!candidate.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object) yield break;
        if (!content.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array) yield break;

        foreach (var part in parts.EnumerateArray())
            if (part.ValueKind == JsonValueKind.Object) yield return part;
    }

    private static ToolCallResult ToToolCall(JsonElement functionCall)
    {
        var name = functionCall.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()! : "";
        return new ToolCallResult
        {
            // Gemini mints no call id. Prefixing the synthetic one with the
            // function name keeps the pairing recoverable when the result comes
            // back as a functionResponse — see ResolveFunctionName.
            Id = $"{name}:{Guid.NewGuid().ToString("N")[..8]}",
            Name = name,
            Arguments = functionCall.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
                ? a : EmptyArgs,
        };
    }

    private static int ReadInt(JsonElement obj, string name, int fallback)
        => obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32() : fallback;
}
