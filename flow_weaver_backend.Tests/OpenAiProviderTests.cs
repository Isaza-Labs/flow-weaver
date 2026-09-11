using System.Net;
using System.Text;
using System.Text.Json;
using ModelLimits = flow_weaver_backend.Dtos.ModelLimits;
using flow_weaver_backend.Services.Ai.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// OpenAiProvider talks raw HTTP to /v1/chat/completions. The parts worth
// pinning are the ones that silently corrupt a conversation when wrong:
// SSE reassembly of streamed tool-call arguments (they arrive as fragments
// across many chunks), the retry policy (transient vs fatal), and the
// temperature quirk that makes GPT-5/o-series reject the whole request.
public class OpenAiProviderTests
{
    private static readonly JsonElement EmptySchema = TestJson.Element("""{"type":"object"}""");

    private static OpenAiProvider Build(
        FakeHttpMessageHandler handler, string apiKey = "sk-test", string? baseUrl = null)
        => new(new HttpClient(handler, disposeHandler: false), apiKey, baseUrl,
            NullLogger<OpenAiProvider>.Instance);

    private static List<LlmMessage> UserSays(string text = "hello")
        => new() { new LlmMessage { Role = "user", Content = text } };

    // Builds an SSE response body from the given `data:` payloads.
    private static FakeHttpMessageHandler Sse(params string[] dataLines)
    {
        var sb = new StringBuilder();
        foreach (var line in dataLines) sb.Append("data: ").Append(line).Append("\n\n");
        return new FakeHttpMessageHandler(HttpStatusCode.OK, sb.ToString(), "text/event-stream");
    }

    private static async Task<List<ChatStreamEvent>> Drain(
        OpenAiProvider provider, List<ToolDefinition>? tools = null, string model = "gpt-4o")
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            UserSays(), tools ?? new(), model, 0.7, default))
        {
            events.Add(e);
        }
        return events;
    }

    private static JsonElement SentBody(FakeHttpMessageHandler handler)
        => TestJson.Element(handler.RequestBodies[0]);

    // ─── output cap ─────────────────────────────────────────────────────

    private static OpenAiProvider BuildWithLimits(FakeHttpMessageHandler handler, string limitsJson)
        => new(new HttpClient(handler, disposeHandler: false), "sk-test", null,
            NullLogger<OpenAiProvider>.Instance, ModelLimits.FromConfig(TestJson.Element(limitsJson)));

    // No limits configured: nothing is sent, OpenAI allows the model's maximum.
    [Fact]
    public async Task NoModelLimitsMeansNoOutputCapIsSent()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"choices":[]}""");

        await Build(handler).ChatAsync(UserSays(), "gpt-4o", 0.7, default);

        var body = SentBody(handler);
        Assert.False(body.TryGetProperty("max_tokens", out _));
        Assert.False(body.TryGetProperty("max_completion_tokens", out _));
    }

    // Older models and OpenAI-compatible gateways only know `max_tokens`; the
    // reasoning family (gpt-5, o-series) rejects it and wants
    // `max_completion_tokens` — the same split that decides temperature.
    [Theory]
    [InlineData("gpt-4o", "max_tokens")]
    [InlineData("gpt-4.1-mini", "max_tokens")]
    [InlineData("gpt-5.4", "max_completion_tokens")]
    [InlineData("o3-mini", "max_completion_tokens")]
    public async Task ConfiguredOutputCapUsesTheParameterTheModelAccepts(string model, string expectedField)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"choices":[]}""");
        var provider = BuildWithLimits(handler, """{"model_limits":{"context_window":128000,"max_output_tokens":8000}}""");

        await provider.ChatAsync(UserSays(), model, 0.7, default);

        var body = SentBody(handler);
        Assert.Equal(8000, body.GetProperty(expectedField).GetInt32());
        var other = expectedField == "max_tokens" ? "max_completion_tokens" : "max_tokens";
        Assert.False(body.TryGetProperty(other, out _));
    }

    // ─── stop reason ────────────────────────────────────────────────────

    // "length" means the model was cut off, and the text carries no sign of it.
    // It arrives on the last content chunk and must reach the runner on every
    // "done" event — the usage-only chunk and the [DONE] sentinel each yield one.
    [Fact]
    public async Task FinishReasonLengthRidesOnEveryDoneEvent()
    {
        var handler = Sse(
            """{"choices":[{"delta":{"content":"Half an ans"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"length"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":4096}}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        var dones = events.Where(e => e.Type == "done").ToList();
        Assert.NotEmpty(dones);
        Assert.All(dones, d => Assert.Equal("length", d.StopReason));
    }

    [Fact]
    public async Task FinishReasonStopIsReportedAsStop()
    {
        var handler = Sse(
            """{"choices":[{"delta":{"content":"Done."},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        Assert.Equal("stop", events.Single(e => e.Type == "done").StopReason);
    }

    // Cut off mid-tool-call the arguments are not JSON. The broken call is
    // dropped and "done" still says why the stream ended, instead of the parse
    // exception escaping the enumerator and ending the turn as a generic error.
    [Fact]
    public async Task AToolCallCutMidArgumentsIsDroppedNotThrown()
    {
        var handler = Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"list_workflows","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"nam"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"length"}]}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        Assert.DoesNotContain(events, e => e.Type == "tool_call");
        Assert.Equal("length", events.Single(e => e.Type == "done").StopReason);
    }

    // ─── request shaping ────────────────────────────────────────────────

    [Fact]
    public async Task PostsToChatCompletionsWithBearerAuth()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"choices":[]}""");

        await Build(handler).ChatAsync(UserSays(), "gpt-4o", 0.7, default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.openai.com/v1/chat/completions", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", req.Headers.Authorization.Parameter);
    }

    // OpenAI-compatible endpoints (Azure, vLLM) are supported by swapping the
    // base URL; a trailing slash must not produce a double slash.
    [Theory]
    [InlineData("https://vllm.internal", "https://vllm.internal/v1/chat/completions")]
    [InlineData("https://vllm.internal/", "https://vllm.internal/v1/chat/completions")]
    public async Task HonoursACustomBaseUrl(string baseUrl, string expected)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"choices":[]}""");

        await Build(handler, baseUrl: baseUrl).ChatAsync(UserSays(), "gpt-4o", 0.7, default);

        Assert.Equal(expected, handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task NonStreamingRequestSetsStreamFalse()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"choices":[]}""");

        await Build(handler).ChatAsync(UserSays(), "gpt-4o", 0.7, default);

        var body = SentBody(handler);
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.Equal("gpt-4o", body.GetProperty("model").GetString());
    }

    // Streaming asks for usage so the caller can bill the turn.
    [Fact]
    public async Task StreamingRequestAsksForUsage()
    {
        var handler = Sse("[DONE]");

        await Drain(Build(handler));

        var body = SentBody(handler);
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    // GPT-5 and the o-series reject any temperature but the default with a
    // 400, which fails the entire request — so the field must be omitted.
    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5-mini")]
    [InlineData("GPT-5.4")]
    [InlineData("o1-preview")]
    [InlineData("o3-mini")]
    [InlineData("o4")]
    public async Task TemperatureIsOmittedForModelsThatRejectIt(string model)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"choices":[]}""");

        await Build(handler).ChatAsync(UserSays(), model, 0.7, default);

        Assert.False(SentBody(handler).TryGetProperty("temperature", out _));
    }

    [Theory]
    [InlineData("gpt-4o")]
    [InlineData("gpt-4.1")]
    [InlineData("llama-3-70b")]
    public async Task TemperatureIsSentForModelsThatHonourIt(string model)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"choices":[]}""");

        await Build(handler).ChatAsync(UserSays(), model, 0.42, default);

        Assert.Equal(0.42, SentBody(handler).GetProperty("temperature").GetDouble(), 3);
    }

    [Fact]
    public async Task ToolsAreMappedToTheFunctionSchema()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"choices":[]}""");
        var tools = new List<ToolDefinition>
        {
            new() { Name = "list_devices", Description = "Lists devices", ParametersSchema = EmptySchema },
        };

        await Build(handler).ChatWithToolsAsync(UserSays(), tools, "gpt-4o", 0.7, default);

        var tool = Assert.Single(SentBody(handler).GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("list_devices", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Lists devices", tool.GetProperty("function").GetProperty("description").GetString());
    }

    [Fact]
    public async Task NoToolsMeansNoToolsField()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"choices":[]}""");

        await Build(handler).ChatAsync(UserSays(), "gpt-4o", 0.7, default);

        Assert.False(SentBody(handler).TryGetProperty("tools", out _));
    }

    // A tool result message must carry tool_call_id, or OpenAI rejects the
    // whole conversation as malformed.
    [Fact]
    public async Task ToolRoleMessagesCarryTheToolCallId()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"choices":[]}""");
        var messages = new List<LlmMessage>
        {
            new() { Role = "tool", Content = "[]", ToolCallId = "call_123" },
        };

        await Build(handler).ChatAsync(messages, "gpt-4o", 0.7, default);

        var msg = Assert.Single(SentBody(handler).GetProperty("messages").EnumerateArray());
        Assert.Equal("tool", msg.GetProperty("role").GetString());
        Assert.Equal("call_123", msg.GetProperty("tool_call_id").GetString());
    }

    // Assistant turns that made tool calls must replay them, otherwise the
    // follow-up tool result has nothing to attach to.
    [Fact]
    public async Task AssistantToolCallsAreReplayedWithSerialisedArguments()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"choices":[]}""");
        var messages = new List<LlmMessage>
        {
            new()
            {
                Role = "assistant",
                Content = null,
                ToolCalls = new()
                {
                    new ToolCallResult
                    {
                        Id = "call_1", Name = "list_devices",
                        Arguments = TestJson.Element("""{"limit":5}"""),
                    },
                },
            },
        };

        await Build(handler).ChatAsync(messages, "gpt-4o", 0.7, default);

        var msg = Assert.Single(SentBody(handler).GetProperty("messages").EnumerateArray());
        Assert.Equal("assistant", msg.GetProperty("role").GetString());
        var call = Assert.Single(msg.GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("call_1", call.GetProperty("id").GetString());
        // Arguments go over the wire as a JSON *string*, not an object.
        Assert.Equal("""{"limit":5}""", call.GetProperty("function").GetProperty("arguments").GetString());
    }

    // ─── non-streaming response parsing ─────────────────────────────────

    [Fact]
    public async Task ParsesContentAndUsage()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":"Hi there"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":11,"completion_tokens":4}}
            """);

        var result = await Build(handler).ChatAsync(UserSays(), "gpt-4o", 0.7, default);

        Assert.Equal("Hi there", result.Content);
        Assert.Equal(11, result.InputTokens);
        Assert.Equal(4, result.OutputTokens);
        Assert.Equal("stop", result.StopReason);
        Assert.Null(result.ToolCalls);
    }

    [Fact]
    public async Task ParsesToolCalls()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":null,"tool_calls":[
              {"id":"call_1","function":{"name":"list_devices","arguments":"{\"limit\":5}"}}]},
              "finish_reason":"tool_calls"}],
             "usage":{"prompt_tokens":1,"completion_tokens":2}}
            """);

        var result = await Build(handler).ChatAsync(UserSays(), "gpt-4o", 0.7, default);

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("list_devices", call.Name);
        Assert.Equal(5, call.Arguments.GetProperty("limit").GetInt32());
        Assert.Equal(string.Empty, result.Content);   // null content normalises to ""
    }

    // A tool call with no arguments must still parse to a valid empty object.
    [Fact]
    public async Task ToolCallWithoutArgumentsBecomesEmptyObject()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"choices":[{"message":{"tool_calls":[{"id":"c1","function":{"name":"ping"}}]}}],
             "usage":{"prompt_tokens":0,"completion_tokens":0}}
            """);

        var result = await Build(handler).ChatAsync(UserSays(), "gpt-4o", 0.7, default);

        Assert.Equal(JsonValueKind.Object, Assert.Single(result.ToolCalls!).Arguments.ValueKind);
    }

    // Degenerate but real responses: an empty `choices` array (content filter)
    // and a missing `usage` block (OpenAI-compatible gateways like vLLM often
    // omit it). Both must degrade to empty values — they used to throw
    // InvalidOperationException from TryGetProperty on an Undefined element.
    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{"content":"hi"}}]}""")]   // no usage
    [InlineData("""{"choices":[{}]}""")]                             // no message
    [InlineData("""{}""")]                                           // nothing at all
    [InlineData("""{"choices":null,"usage":null}""")]
    public async Task DegenerateResponsesDegradeToEmptyInsteadOfThrowing(string json)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, json);

        var result = await Build(handler).ChatAsync(UserSays(), "gpt-4o", 0.7, default);

        Assert.NotNull(result.Content);
        Assert.Equal(0, result.InputTokens);
        Assert.Equal(0, result.OutputTokens);
        Assert.Null(result.ToolCalls);
    }

    // ─── SSE streaming ──────────────────────────────────────────────────

    [Fact]
    public async Task StreamsTextDeltasInOrder()
    {
        var handler = Sse(
            """{"choices":[{"delta":{"content":"Hel"}}]}""",
            """{"choices":[{"delta":{"content":"lo"}}]}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        var deltas = events.Where(e => e.Type == "text_delta").Select(e => e.TextDelta);
        Assert.Equal(new[] { "Hel", "lo" }, deltas);
        Assert.Equal("done", events[^1].Type);
    }

    // Empty content deltas are noise and must not surface as events.
    [Fact]
    public async Task EmptyTextDeltasAreSuppressed()
    {
        var handler = Sse(
            """{"choices":[{"delta":{"content":""}}]}""",
            """{"choices":[{"delta":{"content":"x"}}]}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        Assert.Single(events, e => e.Type == "text_delta");
    }

    // The core streaming contract: tool arguments arrive as fragments spread
    // over many chunks and must be reassembled into one valid JSON document.
    [Fact]
    public async Task ReassemblesFragmentedToolCallArguments()
    {
        var handler = Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"list_devices","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"li"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"mit\":"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"5}"}}]}}]}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        var call = Assert.Single(events, e => e.Type == "tool_call").ToolCall!;
        Assert.Equal("call_1", call.Id);
        Assert.Equal("list_devices", call.Name);
        Assert.Equal(5, call.Arguments.GetProperty("limit").GetInt32());
    }

    // Parallel tool calls are keyed by `index`; mixing their fragments up
    // would produce two corrupt argument blobs.
    [Fact]
    public async Task KeepsParallelToolCallsSeparateByIndex()
    {
        var handler = Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c0","function":{"name":"alpha","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"c1","function":{"name":"beta","arguments":""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"a\":1}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"b\":2}"}}]}}]}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        var calls = events.Where(e => e.Type == "tool_call").Select(e => e.ToolCall!).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(1, calls.Single(c => c.Name == "alpha").Arguments.GetProperty("a").GetInt32());
        Assert.Equal(2, calls.Single(c => c.Name == "beta").Arguments.GetProperty("b").GetInt32());
    }

    // A tool call that never received argument fragments still has to yield a
    // parseable (empty) object rather than throwing at [DONE].
    [Fact]
    public async Task ToolCallWithNoArgumentFragmentsYieldsEmptyObject()
    {
        var handler = Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c0","function":{"name":"ping"}}]}}]}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        var call = Assert.Single(events, e => e.Type == "tool_call").ToolCall!;
        Assert.Equal(JsonValueKind.Object, call.Arguments.ValueKind);
        Assert.Empty(call.Arguments.EnumerateObject());
    }

    // Usage arrives in a final choices-less chunk when include_usage is on.
    [Fact]
    public async Task EmitsUsageFromTheChoicelessFinalChunk()
    {
        var handler = Sse(
            """{"choices":[{"delta":{"content":"hi"}}]}""",
            """{"choices":[],"usage":{"prompt_tokens":7,"completion_tokens":3}}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        var usage = events.First(e => e.Type == "done" && e.InputTokens > 0);
        Assert.Equal(7, usage.InputTokens);
        Assert.Equal(3, usage.OutputTokens);
    }

    [Fact]
    public async Task EmitsUsageWhenAttachedToARegularChunk()
    {
        var handler = Sse(
            """{"choices":[{"delta":{"content":"hi"}}],"usage":{"prompt_tokens":9,"completion_tokens":1}}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        Assert.Contains(events, e => e.Type == "done" && e.InputTokens == 9 && e.OutputTokens == 1);
    }

    // Robustness: the stream must survive the malformed / null-shaped chunks
    // OpenAI genuinely emits, instead of aborting the turn.
    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"choices":[{"delta":null}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop"}]}""")]
    [InlineData("""{"choices":[],"usage":null}""")]
    [InlineData("""{"choices":[{"delta":{"content":null}}]}""")]
    public async Task SurvivesMalformedOrNullChunks(string chunk)
    {
        var handler = Sse(chunk, """{"choices":[{"delta":{"content":"ok"}}]}""", "[DONE]");

        var events = await Drain(Build(handler));

        Assert.Contains(events, e => e.Type == "text_delta" && e.TextDelta == "ok");
        Assert.Equal("done", events[^1].Type);
    }

    // Lines that aren't SSE data frames (comments, blank keep-alives) are skipped.
    [Fact]
    public async Task IgnoresNonDataLines()
    {
        var body = ": keep-alive\n\nevent: ping\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n\ndata: [DONE]\n\n";
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, body, "text/event-stream");

        var events = await Drain(Build(handler));

        Assert.Single(events, e => e.Type == "text_delta");
    }

    // A stream that ends without [DONE] must terminate cleanly, not hang.
    [Fact]
    public async Task StreamTruncatedWithoutDoneTerminatesCleanly()
    {
        var handler = Sse("""{"choices":[{"delta":{"content":"partial"}}]}""");

        var events = await Drain(Build(handler));

        Assert.Equal("partial", Assert.Single(events).TextDelta);
    }

    // ─── retry policy ───────────────────────────────────────────────────

    // Transient upstream failures are retried; the caller sees a normal result.
    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task RetriesTransientStatusCodes(int status)
    {
        var attempts = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return attempts == 1
                ? new HttpResponseMessage((HttpStatusCode)status)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"choices":[{"message":{"content":"ok"}}],"usage":{"prompt_tokens":0,"completion_tokens":0}}""",
                        Encoding.UTF8, "application/json"),
                };
        });

        var result = await Build(handler).ChatAsync(UserSays(), "gpt-4o", 0.7, default);

        Assert.Equal(2, attempts);
        Assert.Equal("ok", result.Content);
    }

    // Client errors are fatal on the first try — retrying a 400/401 just burns
    // latency and repeats the same failure.
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task DoesNotRetryFatalClientErrors(int status)
    {
        var attempts = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("""{"error":{"message":"bad"}}""", Encoding.UTF8, "application/json"),
            };
        });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).ChatAsync(UserSays(), "gpt-4o", 0.7, default));

        Assert.Equal(1, attempts);
    }

    // After exhausting the backoff schedule (3 retries → 4 attempts) the error
    // surfaces instead of looping forever.
    [Fact]
    public async Task GivesUpAfterTheRetryBudgetIsExhausted()
    {
        var attempts = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).ChatAsync(UserSays(), "gpt-4o", 0.7, default));

        Assert.Equal(4, attempts);
    }

    // Network-level failures (DNS, reset, TLS) have no status code but are
    // just as transient as a 503.
    [Fact]
    public async Task RetriesNetworkLevelFailures()
    {
        var attempts = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts == 1) throw new HttpRequestException("connection reset");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"recovered"}}],"usage":{"prompt_tokens":0,"completion_tokens":0}}""",
                    Encoding.UTF8, "application/json"),
            };
        });

        var result = await Build(handler).ChatAsync(UserSays(), "gpt-4o", 0.7, default);

        Assert.Equal(2, attempts);
        Assert.Equal("recovered", result.Content);
    }

    [Fact]
    public void ProviderTypeIsOpenai()
    {
        Assert.Equal("openai", Build(new FakeHttpMessageHandler(HttpStatusCode.OK)).ProviderType);
    }
}
