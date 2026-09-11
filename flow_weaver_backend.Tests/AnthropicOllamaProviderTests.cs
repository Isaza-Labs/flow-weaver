using System.Net;
using System.Text;
using System.Text.Json;
using ModelLimits = flow_weaver_backend.Dtos.ModelLimits;
using flow_weaver_backend.Services.Ai.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Anthropic's native /v1/messages format is block-based rather than
// delta-based: system prompts move out of `messages`, tool results ride inside
// a user turn, and streamed tool arguments arrive as `input_json_delta`
// fragments closed by `content_block_stop`. Each of those conversions is a
// place where a wrong mapping silently breaks the conversation.
public class AnthropicProviderTests
{
    private static readonly JsonElement EmptySchema = TestJson.Element("""{"type":"object"}""");

    private static AnthropicProvider Build(FakeHttpMessageHandler handler, string? baseUrl = null)
        => new(new HttpClient(handler, disposeHandler: false), "sk-ant-test", baseUrl,
            NullLogger<AnthropicProvider>.Instance);

    private static List<LlmMessage> UserSays(string text = "hello")
        => new() { new LlmMessage { Role = "user", Content = text } };

    private static FakeHttpMessageHandler Sse(params string[] dataLines)
    {
        var sb = new StringBuilder();
        foreach (var line in dataLines) sb.Append("data: ").Append(line).Append("\n\n");
        return new FakeHttpMessageHandler(HttpStatusCode.OK, sb.ToString(), "text/event-stream");
    }

    private static async Task<List<ChatStreamEvent>> Drain(AnthropicProvider provider)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            UserSays(), new(), "claude-sonnet-5", 0.7, default))
        {
            events.Add(e);
        }
        return events;
    }

    // stop_reason rides on message_delta next to the usage; "max_tokens" is the
    // one the runner acts on (the hardcoded 4096 makes it reachable).
    [Fact]
    public async Task StopReasonMaxTokensReachesTheDoneEvent()
    {
        var handler = Sse(
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":"Half"}}""",
            """{"type":"message_delta","delta":{"stop_reason":"max_tokens"},"usage":{"output_tokens":4096}}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        Assert.Equal("max_tokens", events.Single(e => e.Type == "done").StopReason);
    }

    [Fact]
    public async Task StopReasonEndTurnIsReportedAsIs()
    {
        var handler = Sse(
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        Assert.Equal("end_turn", events.Single(e => e.Type == "done").StopReason);
    }

    private static JsonElement SentBody(FakeHttpMessageHandler handler)
        => TestJson.Element(handler.RequestBodies[0]);

    private static FakeHttpMessageHandler Empty()
        => new(HttpStatusCode.OK, """{"content":[],"usage":{"input_tokens":0,"output_tokens":0}}""");

    // ─── request shaping ────────────────────────────────────────────────

    [Fact]
    public async Task PostsToMessagesWithApiKeyAndVersionHeaders()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "claude-sonnet-5", 0.7, default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", req.RequestUri!.ToString());
        Assert.Equal("sk-ant-test", Assert.Single(req.Headers.GetValues("x-api-key")));
        Assert.Equal("2023-06-01", Assert.Single(req.Headers.GetValues("anthropic-version")));
    }

    [Fact]
    public async Task HonoursACustomBaseUrlAndTrimsTrailingSlash()
    {
        var handler = Empty();

        await Build(handler, "https://proxy.internal/").ChatAsync(UserSays(), "claude-sonnet-5", 0.7, default);

        Assert.Equal("https://proxy.internal/v1/messages", handler.Requests[0].RequestUri!.ToString());
    }

    // Anthropic takes the system prompt as a top-level field, NOT as a message.
    // Leaving it in `messages` is a 400.
    [Fact]
    public async Task SystemPromptIsHoistedOutOfMessages()
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are a network engineer." },
            new() { Role = "user", Content = "hi" },
        };

        await Build(handler).ChatAsync(messages, "claude-sonnet-5", 0.7, default);

        var body = SentBody(handler);
        Assert.Equal("You are a network engineer.", body.GetProperty("system").GetString());
        var msg = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", msg.GetProperty("role").GetString());
    }

    [Fact]
    public async Task NoSystemMessageMeansNoSystemField()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "claude-sonnet-5", 0.7, default);

        Assert.False(SentBody(handler).TryGetProperty("system", out _));
    }

    // max_tokens is required on every request. It used to be a flat 8192 for every
    // model, which quietly cut long answers on models that allow far more.
    [Fact]
    public async Task SendsMaxTokens()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "claude-sonnet-5", 0.33, default);

        Assert.Equal(16_000, SentBody(handler).GetProperty("max_tokens").GetInt32());
    }

    // Sampling was removed on the current model families: sending `temperature` to
    // Sonnet 5 is a 400, and this provider used to send it on every request — so
    // every call to a current Claude model failed. Older models still take it.
    [Theory]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("claude-opus-5", false)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-3-5-sonnet-20241022", true)]
    public async Task TemperatureIsSentOnlyToModelsThatAcceptIt(string model, bool expected)
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), model, 0.33, default);

        var body = SentBody(handler);
        Assert.Equal(expected, body.TryGetProperty("temperature", out var t));
        if (expected) Assert.Equal(0.33, t.GetDouble(), 3);
    }

    // Opus 4 and 4.1 stop at 32k output while the rest of the 4.x line takes 64k;
    // above a model's own ceiling the request is a 400, so the cap wins.
    [Theory]
    [InlineData("claude-3-5-sonnet-20241022", 8_192)]
    [InlineData("claude-3-opus-20240229", 4_096)]
    [InlineData("claude-opus-4-1", 16_000)] // cap 32k, above the non-streaming default
    [InlineData("claude-opus-5", 16_000)]   // unlisted: the default stands
    public async Task MaxTokensIsClampedToTheModelsOutputCap(string model, int expected)
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), model, 0.2, default);

        Assert.Equal(expected, SentBody(handler).GetProperty("max_tokens").GetInt32());
    }

    // On a stream the default is 64k, which is where Opus 4 and 4.1 — the only 4.x
    // models that stop at 32k — would otherwise 400 on every request.
    [Theory]
    [InlineData("claude-opus-4-1", 32_000)]
    [InlineData("claude-opus-4-0", 32_000)]
    [InlineData("claude-opus-5", 64_000)]
    public async Task StreamingMaxTokensIsClampedToTheModelsOutputCap(string model, int expected)
    {
        var handler = Sse("[DONE]");

        await foreach (var _ in Build(handler).ChatWithToolsStreamAsync(
            UserSays(), new(), model, 0.2, default)) { }

        Assert.Equal(expected, SentBody(handler).GetProperty("max_tokens").GetInt32());
    }

    // A conversation that starts on an assistant turn is a 400 ("first message must
    // use the user role"), so one is put in front of it rather than failing the turn.
    [Fact]
    public async Task AHistoryStartingOnAnAssistantTurnGetsAUserTurnInFront()
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new() { Role = "assistant", Content = "as I was saying" },
            new() { Role = "user", Content = "go on" },
        };

        await Build(handler).ChatAsync(messages, "claude-opus-5", 0.2, default);

        var sent = SentBody(handler).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user"], sent.Select(m => m.GetProperty("role").GetString()));
    }

    // Anthropic's docs put the version in the base URL, so a pasted
    // "https://api.anthropic.com/v1" must not become /v1/v1/messages.
    [Theory]
    [InlineData(null, "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://gw.internal/anthropic", "https://gw.internal/anthropic/v1/messages")]
    public async Task RequestUrlDoesNotRepeatAVersionSegmentTheBaseUrlAlreadyHas(string? baseUrl, string expected)
    {
        var handler = Empty();

        await Build(handler, baseUrl).ChatAsync(UserSays(), "claude-opus-5", 0.2, default);

        Assert.Equal(expected, handler.Requests[0].RequestUri!.ToString());
    }

    // Every system message counts. Only the first one used to be sent and the rest
    // were dropped, so a prompt assembled from several messages lost most of itself.
    [Fact]
    public async Task AllSystemMessagesReachTheSystemField()
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "base prompt" },
            new() { Role = "system", Content = "profile: net" },
            new() { Role = "user", Content = "hi" },
        };

        await Build(handler).ChatAsync(messages, "claude-opus-5", 0.2, default);

        Assert.Equal("base prompt\n\nprofile: net", SentBody(handler).GetProperty("system").GetString());
    }

    // Anthropic requires alternating roles, so a parallel tool round has to arrive as
    // one user turn holding both results — it used to be sent as two user turns.
    [Fact]
    public async Task AParallelToolRoundArrivesAsOneUserTurn()
    {
        var handler = Empty();
        var args = TestJson.Element("""{"ip":"1.1.1.1"}""");
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "ping both" },
            new()
            {
                Role = "assistant",
                Content = "on it",
                ToolCalls = new()
                {
                    new ToolCallResult { Id = "toolu_1", Name = "ping", Arguments = args },
                    new ToolCallResult { Id = "toolu_2", Name = "ping", Arguments = args },
                },
            },
            new() { Role = "tool", Content = "ok", ToolCallId = "toolu_1" },
            new() { Role = "tool", Content = "ok", ToolCallId = "toolu_2" },
        };

        await Build(handler).ChatAsync(messages, "claude-opus-5", 0.2, default);

        var sent = SentBody(handler).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(3, sent.Count);
        Assert.Equal(["user", "assistant", "user"], sent.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal(2, sent[2].GetProperty("content").GetArrayLength());
    }

    // A provider row with Config.model_limits decides max_tokens; the default is
    // only for rows that say nothing.
    [Fact]
    public async Task ConfiguredModelLimitsSetMaxTokens()
    {
        var handler = Empty();
        var limits = ModelLimits.FromConfig(TestJson.Element("""{"model_limits":{"context_window":200000,"max_output_tokens":16000}}"""));
        var provider = new AnthropicProvider(
            new HttpClient(handler, disposeHandler: false), "sk-ant-test", null,
            NullLogger<AnthropicProvider>.Instance, limits);

        await provider.ChatAsync(UserSays(), "claude-sonnet-5", 0.2, default);

        Assert.Equal(16000, SentBody(handler).GetProperty("max_tokens").GetInt32());
    }

    // A tool result is a `tool_result` block inside a USER turn — Anthropic has
    // no "tool" role.
    [Fact]
    public async Task ToolResultsBecomeToolResultBlocksInAUserTurn()
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new() { Role = "tool", Content = "[]", ToolCallId = "toolu_1" },
        };

        await Build(handler).ChatAsync(messages, "claude-sonnet-5", 0.7, default);

        var msg = Assert.Single(SentBody(handler).GetProperty("messages").EnumerateArray());
        Assert.Equal("user", msg.GetProperty("role").GetString());
        var block = Assert.Single(msg.GetProperty("content").EnumerateArray());
        Assert.Equal("tool_result", block.GetProperty("type").GetString());
        Assert.Equal("toolu_1", block.GetProperty("tool_use_id").GetString());
    }

    // Assistant tool calls become `tool_use` blocks; arguments stay a JSON
    // object (unlike OpenAI, which wants a string).
    [Fact]
    public async Task AssistantToolCallsBecomeToolUseBlocksWithObjectInput()
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "which devices?" },
            new()
            {
                Role = "assistant",
                Content = "Let me check.",
                ToolCalls = new()
                {
                    new ToolCallResult
                    {
                        Id = "toolu_1", Name = "list_devices",
                        Arguments = TestJson.Element("""{"limit":5}"""),
                    },
                },
            },
        };

        await Build(handler).ChatAsync(messages, "claude-sonnet-5", 0.7, default);

        var blocks = SentBody(handler).GetProperty("messages").EnumerateArray().ToList()[1]
            .GetProperty("content").EnumerateArray().ToList();
        Assert.Equal("text", blocks[0].GetProperty("type").GetString());
        Assert.Equal("tool_use", blocks[1].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, blocks[1].GetProperty("input").ValueKind);
        Assert.Equal(5, blocks[1].GetProperty("input").GetProperty("limit").GetInt32());
    }

    // With no text alongside the tool call, only the tool_use block is sent.
    [Fact]
    public async Task AssistantToolCallWithoutTextEmitsOnlyTheToolUseBlock()
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "ping it" },
            new()
            {
                Role = "assistant",
                Content = null,
                ToolCalls = new()
                {
                    new ToolCallResult { Id = "t1", Name = "ping", Arguments = TestJson.Element("{}") },
                },
            },
        };

        await Build(handler).ChatAsync(messages, "claude-sonnet-5", 0.7, default);

        var sent = SentBody(handler).GetProperty("messages").EnumerateArray().ToList();
        var blocks = sent[1].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal("tool_use", Assert.Single(blocks).GetProperty("type").GetString());
    }

    // Anthropic names the schema field `input_schema`, not `parameters`.
    [Fact]
    public async Task ToolsUseInputSchema()
    {
        var handler = Empty();
        var tools = new List<ToolDefinition>
        {
            new() { Name = "list_devices", Description = "Lists", ParametersSchema = EmptySchema },
        };

        await Build(handler).ChatWithToolsAsync(UserSays(), tools, "claude-sonnet-5", 0.7, default);

        var tool = Assert.Single(SentBody(handler).GetProperty("tools").EnumerateArray());
        Assert.Equal("list_devices", tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
    }

    // ─── response parsing ───────────────────────────────────────────────

    // Anthropic can split a reply across several text blocks; they must be
    // concatenated rather than only the first one kept.
    [Fact]
    public async Task ConcatenatesMultipleTextBlocks()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"content":[{"type":"text","text":"Hello "},{"type":"text","text":"world"}],
             "stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":2}}
            """);

        var result = await Build(handler).ChatAsync(UserSays(), "claude-sonnet-5", 0.7, default);

        Assert.Equal("Hello world", result.Content);
        Assert.Equal(10, result.InputTokens);
        Assert.Equal(2, result.OutputTokens);
        Assert.Equal("end_turn", result.StopReason);
    }

    [Fact]
    public async Task ParsesToolUseBlocks()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"content":[{"type":"tool_use","id":"toolu_1","name":"list_devices","input":{"limit":5}}],
             "stop_reason":"tool_use","usage":{"input_tokens":1,"output_tokens":1}}
            """);

        var result = await Build(handler).ChatAsync(UserSays(), "claude-sonnet-5", 0.7, default);

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("list_devices", call.Name);
        Assert.Equal(5, call.Arguments.GetProperty("limit").GetInt32());
    }

    // Missing `usage` must not take the call down (it used to throw from
    // TryGetProperty on an Undefined element).
    [Theory]
    [InlineData("""{"content":[{"type":"text","text":"hi"}]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"content":null,"usage":null}""")]
    public async Task DegenerateResponsesDegradeToEmptyInsteadOfThrowing(string json)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, json);

        var result = await Build(handler).ChatAsync(UserSays(), "claude-sonnet-5", 0.7, default);

        Assert.NotNull(result.Content);
        Assert.Equal(0, result.InputTokens);
        Assert.Equal(0, result.OutputTokens);
    }

    // Anthropic has no retry layer — an upstream error surfaces immediately.
    [Theory]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task UpstreamErrorsThrow(int status)
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode)status, """{"error":{"message":"nope"}}""");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).ChatAsync(UserSays(), "claude-sonnet-5", 0.7, default));
    }

    // ─── SSE streaming ──────────────────────────────────────────────────

    [Fact]
    public async Task StreamsTextDeltas()
    {
        var handler = Sse(
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hel"}}""",
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":"lo"}}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        Assert.Equal(new[] { "Hel", "lo" }, events.Where(e => e.Type == "text_delta").Select(e => e.TextDelta));
    }

    // The block lifecycle: start declares id+name, input_json_delta carries
    // argument fragments, stop closes and emits the assembled call.
    [Fact]
    public async Task AssemblesToolCallAcrossTheBlockLifecycle()
    {
        var handler = Sse(
            """{"type":"content_block_start","content_block":{"type":"tool_use","id":"toolu_1","name":"list_devices"}}""",
            """{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{\"li"}}""",
            """{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"mit\":5}"}}""",
            """{"type":"content_block_stop"}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        var call = Assert.Single(events, e => e.Type == "tool_call").ToolCall!;
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("list_devices", call.Name);
        Assert.Equal(5, call.Arguments.GetProperty("limit").GetInt32());
    }

    // Two sequential tool_use blocks must not bleed arguments into each other.
    [Fact]
    public async Task SequentialToolBlocksDoNotShareArguments()
    {
        var handler = Sse(
            """{"type":"content_block_start","content_block":{"type":"tool_use","id":"t1","name":"alpha"}}""",
            """{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{\"a\":1}"}}""",
            """{"type":"content_block_stop"}""",
            """{"type":"content_block_start","content_block":{"type":"tool_use","id":"t2","name":"beta"}}""",
            """{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{\"b\":2}"}}""",
            """{"type":"content_block_stop"}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        var calls = events.Where(e => e.Type == "tool_call").Select(e => e.ToolCall!).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(1, calls[0].Arguments.GetProperty("a").GetInt32());
        Assert.Equal(2, calls[1].Arguments.GetProperty("b").GetInt32());
        Assert.False(calls[1].Arguments.TryGetProperty("a", out _));
    }

    // A text block's `content_block_stop` must not emit a phantom tool call.
    [Fact]
    public async Task ContentBlockStopForTextEmitsNoToolCall()
    {
        var handler = Sse(
            """{"type":"content_block_start","content_block":{"type":"text"}}""",
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":"hi"}}""",
            """{"type":"content_block_stop"}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        Assert.DoesNotContain(events, e => e.Type == "tool_call");
    }

    [Fact]
    public async Task ToolBlockWithNoFragmentsYieldsEmptyArguments()
    {
        var handler = Sse(
            """{"type":"content_block_start","content_block":{"type":"tool_use","id":"t1","name":"ping"}}""",
            """{"type":"content_block_stop"}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        var call = Assert.Single(events, e => e.Type == "tool_call").ToolCall!;
        Assert.Equal(JsonValueKind.Object, call.Arguments.ValueKind);
        Assert.Empty(call.Arguments.EnumerateObject());
    }

    [Fact]
    public async Task MessageDeltaEmitsOutputTokens()
    {
        var handler = Sse(
            """{"type":"message_delta","usage":{"output_tokens":42}}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        var done = Assert.Single(events, e => e.Type == "done");
        Assert.Equal(42, done.OutputTokens);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"type":"unknown_event"}""")]
    [InlineData("""{"type":"content_block_delta"}""")]
    [InlineData("""{"no_type":true}""")]
    public async Task SurvivesUnknownOrMalformedEvents(string chunk)
    {
        var handler = Sse(chunk,
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":"ok"}}""",
            "[DONE]");

        var events = await Drain(Build(handler));

        Assert.Contains(events, e => e.Type == "text_delta" && e.TextDelta == "ok");
    }

    [Fact]
    public void ProviderTypeIsAnthropic()
        => Assert.Equal("anthropic", Build(Empty()).ProviderType);
}

// Ollama speaks newline-delimited JSON (not SSE) against /api/chat, and
// terminates on a `done: true` chunk that also carries the token counts.
public class OllamaProviderTests
{
    private static readonly JsonElement EmptySchema = TestJson.Element("""{"type":"object"}""");

    private static OllamaProvider Build(FakeHttpMessageHandler handler, string? baseUrl = null)
        => new(new HttpClient(handler, disposeHandler: false), baseUrl,
            NullLogger<OllamaProvider>.Instance);

    private static List<LlmMessage> UserSays(string text = "hello")
        => new() { new LlmMessage { Role = "user", Content = text } };

    // NDJSON, one object per line — Ollama's streaming wire format.
    private static FakeHttpMessageHandler Ndjson(params string[] lines)
        => new(HttpStatusCode.OK, string.Join("\n", lines) + "\n", "application/x-ndjson");

    private static async Task<List<ChatStreamEvent>> Drain(OllamaProvider provider)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            UserSays(), new(), "llama3.1", 0.7, default))
        {
            events.Add(e);
        }
        return events;
    }

    // Without num_ctx Ollama runs the model at its default window and drops the
    // top of a prompt that does not fit. Configured limits go out as num_ctx and
    // num_predict under `options`; a row without them sends nothing new.
    [Fact]
    public async Task ConfiguredModelLimitsBecomeNumCtxAndNumPredict()
    {
        var handler = Empty();
        var limits = ModelLimits.FromConfig(TestJson.Element("""{"model_limits":{"context_window":32768,"max_output_tokens":4096}}"""));
        var provider = new OllamaProvider(
            new HttpClient(handler, disposeHandler: false), "http://ollama:11434",
            NullLogger<OllamaProvider>.Instance, limits);

        await provider.ChatAsync(UserSays(), "llama3.1", 0.2, default);

        var options = SentBody(handler).GetProperty("options");
        Assert.Equal(32768, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(4096, options.GetProperty("num_predict").GetInt32());
    }

    // With no model_limits, num_ctx falls back to a window a real prompt fits in
    // rather than being omitted: omitting it left the daemon running the model at its
    // own small window, dropping the front of the prompt — the system prompt — in
    // silence. num_predict stays unset, so output length is still the model's call.
    [Fact]
    public async Task NoModelLimitsStillSendsADefaultNumCtx()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "llama3.1", 0.2, default);

        var options = SentBody(handler).GetProperty("options");
        Assert.Equal(OllamaProvider.DefaultNumCtx, options.GetProperty("num_ctx").GetInt32());
        Assert.False(options.TryGetProperty("num_predict", out _));
    }

    // done_reason "length" is num_predict running out — the runner's cut-off case.
    [Fact]
    public async Task DoneReasonLengthReachesTheDoneEvent()
    {
        var handler = Ndjson(
            """{"message":{"content":"Half"},"done":false}""",
            """{"message":{"content":""},"done":true,"done_reason":"length","prompt_eval_count":5,"eval_count":128}""");

        var events = await Drain(Build(handler));

        Assert.Equal("length", events.Single(e => e.Type == "done").StopReason);
    }

    private static JsonElement SentBody(FakeHttpMessageHandler handler)
        => TestJson.Element(handler.RequestBodies[0]);

    private static FakeHttpMessageHandler Empty()
        => new(HttpStatusCode.OK, """{"message":{"content":""},"prompt_eval_count":0,"eval_count":0}""");

    // ─── request shaping ────────────────────────────────────────────────

    [Fact]
    public async Task PostsToApiChatOnTheDefaultLocalhostPort()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "llama3.1", 0.7, default);

        Assert.Equal("http://localhost:11434/api/chat", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task HonoursACustomBaseUrlAndTrimsTrailingSlash()
    {
        var handler = Empty();

        await Build(handler, "http://gpu-box:11434/").ChatAsync(UserSays(), "llama3.1", 0.7, default);

        Assert.Equal("http://gpu-box:11434/api/chat", handler.Requests[0].RequestUri!.ToString());
    }

    // Ollama nests temperature under `options`, not at the top level.
    [Fact]
    public async Task TemperatureGoesUnderOptions()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "llama3.1", 0.25, default);

        var body = SentBody(handler);
        Assert.Equal(0.25, body.GetProperty("options").GetProperty("temperature").GetDouble(), 3);
        Assert.False(body.TryGetProperty("temperature", out _));
    }

    // Null content must serialise as "" — Ollama rejects a null content field.
    [Fact]
    public async Task NullContentIsSentAsEmptyString()
    {
        var handler = Empty();
        var messages = new List<LlmMessage> { new() { Role = "assistant", Content = null } };

        await Build(handler).ChatAsync(messages, "llama3.1", 0.7, default);

        var msg = Assert.Single(SentBody(handler).GetProperty("messages").EnumerateArray());
        Assert.Equal("", msg.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ToolsAreMappedToTheFunctionSchema()
    {
        var handler = Empty();
        var tools = new List<ToolDefinition>
        {
            new() { Name = "list_devices", Description = "Lists", ParametersSchema = EmptySchema },
        };

        await Build(handler).ChatWithToolsAsync(UserSays(), tools, "llama3.1", 0.7, default);

        var tool = Assert.Single(SentBody(handler).GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("list_devices", tool.GetProperty("function").GetProperty("name").GetString());
    }

    // ─── response parsing ───────────────────────────────────────────────

    [Fact]
    public async Task ParsesContentAndEvalCounts()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"message":{"content":"Hi"},"prompt_eval_count":12,"eval_count":3,"done_reason":"stop"}
            """);

        var result = await Build(handler).ChatAsync(UserSays(), "llama3.1", 0.7, default);

        Assert.Equal("Hi", result.Content);
        Assert.Equal(12, result.InputTokens);
        Assert.Equal(3, result.OutputTokens);
        Assert.Equal("stop", result.StopReason);
    }

    // Ollama supplies no tool-call id, so the provider mints a short one.
    [Fact]
    public async Task ParsesToolCallsAndSynthesizesAnId()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"message":{"content":"","tool_calls":[
              {"function":{"name":"list_devices","arguments":{"limit":5}}}]},
             "prompt_eval_count":1,"eval_count":1}
            """);

        var result = await Build(handler).ChatAsync(UserSays(), "llama3.1", 0.7, default);

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("list_devices", call.Name);
        Assert.Equal(5, call.Arguments.GetProperty("limit").GetInt32());
        Assert.Equal(12, call.Id.Length);
        Assert.NotEmpty(call.Id);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"message":null}""")]
    [InlineData("""{"message":{}}""")]
    [InlineData("""{"message":{"tool_calls":[{}]}}""")]
    public async Task DegenerateResponsesDegradeToEmptyInsteadOfThrowing(string json)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, json);

        var result = await Build(handler).ChatAsync(UserSays(), "llama3.1", 0.7, default);

        Assert.NotNull(result.Content);
        Assert.Equal(0, result.InputTokens);
    }

    [Fact]
    public async Task UpstreamErrorThrows()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "boom");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).ChatAsync(UserSays(), "llama3.1", 0.7, default));
    }

    // ─── NDJSON streaming ───────────────────────────────────────────────

    [Fact]
    public async Task StreamsContentChunksAsTextDeltas()
    {
        var handler = Ndjson(
            """{"message":{"content":"Hel"},"done":false}""",
            """{"message":{"content":"lo"},"done":false}""",
            """{"message":{"content":""},"done":true,"prompt_eval_count":5,"eval_count":2}""");

        var events = await Drain(Build(handler));

        Assert.Equal(new[] { "Hel", "lo" }, events.Where(e => e.Type == "text_delta").Select(e => e.TextDelta));
    }

    // The terminating chunk carries the token accounting for the whole turn.
    [Fact]
    public async Task DoneChunkCarriesTokenCounts()
    {
        var handler = Ndjson(
            """{"message":{"content":"hi"},"done":false}""",
            """{"message":{"content":""},"done":true,"prompt_eval_count":9,"eval_count":4}""");

        var events = await Drain(Build(handler));

        var done = Assert.Single(events, e => e.Type == "done");
        Assert.Equal(9, done.InputTokens);
        Assert.Equal(4, done.OutputTokens);
    }

    // Anything after `done: true` must not be read — the turn is over.
    [Fact]
    public async Task StopsReadingAfterTheDoneChunk()
    {
        var handler = Ndjson(
            """{"message":{"content":"a"},"done":true,"prompt_eval_count":1,"eval_count":1}""",
            """{"message":{"content":"SHOULD NOT APPEAR"},"done":false}""");

        var events = await Drain(Build(handler));

        Assert.DoesNotContain(events, e => e.TextDelta == "SHOULD NOT APPEAR");
        Assert.Equal("done", events[^1].Type);
    }

    [Fact]
    public async Task StreamsToolCalls()
    {
        var handler = Ndjson(
            """{"message":{"content":"","tool_calls":[{"function":{"name":"ping","arguments":{"host":"a"}}}]},"done":false}""",
            """{"message":{"content":""},"done":true,"prompt_eval_count":1,"eval_count":1}""");

        var events = await Drain(Build(handler));

        var call = Assert.Single(events, e => e.Type == "tool_call").ToolCall!;
        Assert.Equal("ping", call.Name);
        Assert.Equal("a", call.Arguments.GetProperty("host").GetString());
    }

    // Malformed lines are logged and skipped rather than aborting the stream.
    [Theory]
    [InlineData("not json")]
    [InlineData("""{"message":null,"done":false}""")]
    [InlineData("""{"done":false}""")]
    public async Task SurvivesMalformedOrEmptyChunks(string chunk)
    {
        var handler = Ndjson(chunk,
            """{"message":{"content":"ok"},"done":false}""",
            """{"message":{"content":""},"done":true,"prompt_eval_count":0,"eval_count":0}""");

        var events = await Drain(Build(handler));

        Assert.Contains(events, e => e.Type == "text_delta" && e.TextDelta == "ok");
        Assert.Equal("done", events[^1].Type);
    }

    // A stream that dies before `done: true` terminates instead of hanging.
    [Fact]
    public async Task TruncatedStreamTerminatesCleanly()
    {
        var handler = Ndjson("""{"message":{"content":"partial"},"done":false}""");

        var events = await Drain(Build(handler));

        Assert.Equal("partial", Assert.Single(events).TextDelta);
    }

    [Fact]
    public void ProviderTypeIsOllama()
        => Assert.Equal("ollama", Build(Empty()).ProviderType);
}
