using System.Net;
using System.Text;
using System.Text.Json;
using ModelLimits = flow_weaver_backend.Dtos.ModelLimits;
using flow_weaver_backend.Services.Ai.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Gemini's generateContent format diverges from both of the other vendors in
// ways that silently break a conversation when mapped wrong: the assistant role
// is "model", the system prompt is a separate `systemInstruction`, tool results
// are matched by function NAME (there are no call ids), and the parameter schema
// is validated against a narrow OpenAPI subset that 400s on any extra keyword.
public class GeminiProviderTests
{
    private static readonly JsonElement EmptySchema = TestJson.Element("""{"type":"object"}""");

    private static GeminiProvider Build(FakeHttpMessageHandler handler, string? baseUrl = null)
        => new(new HttpClient(handler, disposeHandler: false), "AIza-test", baseUrl,
            NullLogger<GeminiProvider>.Instance);

    private static List<LlmMessage> UserSays(string text = "hello")
        => new() { new LlmMessage { Role = "user", Content = text } };

    private static FakeHttpMessageHandler Sse(params string[] dataLines)
    {
        var sb = new StringBuilder();
        foreach (var line in dataLines) sb.Append("data: ").Append(line).Append("\n\n");
        return new FakeHttpMessageHandler(HttpStatusCode.OK, sb.ToString(), "text/event-stream");
    }

    private static async Task<List<ChatStreamEvent>> Drain(GeminiProvider provider)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            UserSays(), new(), "gemini-2.5-pro", 0.7, default))
        {
            events.Add(e);
        }
        return events;
    }

    // finishReason rides on the last chunk's candidate; MAX_TOKENS is the
    // runner's cut-off case (the const 4096 makes it reachable).
    [Fact]
    public async Task FinishReasonMaxTokensReachesTheSynthesisedDone()
    {
        var handler = Sse(
            """{"candidates":[{"content":{"parts":[{"text":"Half"}]}}]}""",
            """{"candidates":[{"content":{"parts":[{"text":" an ans"}]},"finishReason":"MAX_TOKENS"}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":4096}}""");

        var events = await Drain(Build(handler));

        Assert.Equal("MAX_TOKENS", events.Single(e => e.Type == "done").StopReason);
    }

    private static JsonElement SentBody(FakeHttpMessageHandler handler)
        => TestJson.Element(handler.RequestBodies[0]);

    private static FakeHttpMessageHandler Empty()
        => new(HttpStatusCode.OK, """
            {"candidates":[{"content":{"role":"model","parts":[{"text":""}]},"finishReason":"STOP"}],
             "usageMetadata":{"promptTokenCount":0,"candidatesTokenCount":0}}
            """);

    private static List<JsonElement> Contents(FakeHttpMessageHandler handler)
        => SentBody(handler).GetProperty("contents").EnumerateArray().ToList();

    // ─── request shaping ────────────────────────────────────────────────

    [Fact]
    public async Task PostsToGenerateContentWithTheApiKeyHeader()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent",
            req.RequestUri!.ToString());
        Assert.Equal("AIza-test", Assert.Single(req.Headers.GetValues("x-goog-api-key")));
    }

    // The key must not ride in the query string, where proxies log it.
    [Fact]
    public async Task ApiKeyIsNotPutInTheUrl()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default);

        Assert.DoesNotContain("AIza-test", handler.Requests[0].RequestUri!.ToString());
    }

    // An `Authorization: Bearer <key>` — the reflex from every other vendor, and
    // what OpenAiProvider does — makes Google read the request as OAuth, ignore
    // x-goog-api-key and reject it as an invalid token. The header must stay off.
    [Fact]
    public async Task NoAuthorizationHeaderIsSent()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default);

        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task StreamingUsesStreamGenerateContentWithSseAlt()
    {
        var handler = Sse("""{"candidates":[{"content":{"parts":[{"text":"hi"}]}}]}""");

        await Drain(Build(handler));

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task HonoursACustomBaseUrlAndTrimsTrailingSlash()
    {
        var handler = Empty();

        await Build(handler, "https://proxy.internal/").ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default);

        Assert.Equal("https://proxy.internal/v1beta/models/gemini-2.5-pro:generateContent",
            handler.Requests[0].RequestUri!.ToString());
    }

    // The model id is part of the path, so a value copied with the collection
    // prefix must not produce ".../models/models/gemini-2.5-pro".
    [Fact]
    public async Task ModelsPrefixOnTheModelIdIsNotDoubled()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "models/gemini-2.5-flash", 0.7, default);

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
            handler.Requests[0].RequestUri!.ToString());
    }

    // Gemini takes the system prompt as `systemInstruction`; leaving it in
    // `contents` makes the model treat it as a user turn.
    [Fact]
    public async Task SystemPromptIsHoistedIntoSystemInstruction()
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are a network engineer." },
            new() { Role = "user", Content = "hi" },
        };

        await Build(handler).ChatAsync(messages, "gemini-2.5-pro", 0.7, default);

        var body = SentBody(handler);
        Assert.Equal("You are a network engineer.",
            body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var content = Assert.Single(body.GetProperty("contents").EnumerateArray());
        Assert.Equal("user", content.GetProperty("role").GetString());
    }

    [Fact]
    public async Task NoSystemMessageMeansNoSystemInstructionField()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default);

        Assert.False(SentBody(handler).TryGetProperty("systemInstruction", out _));
    }

    [Fact]
    public async Task TemperatureAndMaxTokensGoUnderGenerationConfig()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.33, default);

        var cfg = SentBody(handler).GetProperty("generationConfig");
        Assert.Equal(0.33, cfg.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(GeminiProvider.DefaultMaxOutputTokens, cfg.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(8192, GeminiProvider.DefaultMaxOutputTokens);
    }

    [Fact]
    public async Task ConfiguredModelLimitsSetMaxOutputTokens()
    {
        var handler = Empty();
        var limits = ModelLimits.FromConfig(TestJson.Element("""{"model_limits":{"context_window":1000000,"max_output_tokens":32768}}"""));
        var provider = new GeminiProvider(
            new HttpClient(handler, disposeHandler: false), "AIza-test", null,
            NullLogger<GeminiProvider>.Instance, limits);

        await provider.ChatAsync(UserSays(), "gemini-2.5-pro", 0.2, default);

        Assert.Equal(32768, SentBody(handler).GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
    }

    // "assistant" is not a role Gemini knows — it is "model".
    [Fact]
    public async Task AssistantTurnsBecomeTheModelRole()
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "hi" },
            new() { Role = "assistant", Content = "hello" },
        };

        await Build(handler).ChatAsync(messages, "gemini-2.5-pro", 0.7, default);

        var contents = Contents(handler);
        Assert.Equal("user", contents[0].GetProperty("role").GetString());
        Assert.Equal("model", contents[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task AssistantToolCallsBecomeFunctionCallParts()
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new()
            {
                Role = "assistant",
                Content = "Let me check.",
                ToolCalls = new()
                {
                    new ToolCallResult
                    {
                        Id = "list_devices:abc123",
                        Name = "list_devices",
                        Arguments = TestJson.Element("""{"limit":5}"""),
                    },
                },
            },
        };

        await Build(handler).ChatAsync(messages, "gemini-2.5-pro", 0.7, default);

        var parts = Assert.Single(Contents(handler)).GetProperty("parts").EnumerateArray().ToList();
        Assert.Equal("Let me check.", parts[0].GetProperty("text").GetString());
        var call = parts[1].GetProperty("functionCall");
        Assert.Equal("list_devices", call.GetProperty("name").GetString());
        Assert.Equal(5, call.GetProperty("args").GetProperty("limit").GetInt32());
    }

    // A tool result is a functionResponse part in a USER turn, keyed by the
    // function name recovered from the assistant turn that requested it.
    [Fact]
    public async Task ToolResultsBecomeFunctionResponsesNamedFromTheAssistantTurn()
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new()
            {
                Role = "assistant",
                ToolCalls = new()
                {
                    new ToolCallResult
                    {
                        Id = "opaque-id", Name = "list_devices", Arguments = TestJson.Element("{}"),
                    },
                },
            },
            new() { Role = "tool", Content = """{"count":2}""", ToolCallId = "opaque-id" },
        };

        await Build(handler).ChatAsync(messages, "gemini-2.5-pro", 0.7, default);

        var toolTurn = Contents(handler)[1];
        Assert.Equal("user", toolTurn.GetProperty("role").GetString());
        var fr = Assert.Single(toolTurn.GetProperty("parts").EnumerateArray()).GetProperty("functionResponse");
        Assert.Equal("list_devices", fr.GetProperty("name").GetString());
        Assert.Equal(2, fr.GetProperty("response").GetProperty("count").GetInt32());
    }

    // When the assistant turn is no longer in the window, the name still has to
    // be recoverable — that is what the "<name>:<hex>" id shape is for.
    [Fact]
    public async Task FunctionNameFallsBackToTheIdPrefixWithoutTheAssistantTurn()
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new() { Role = "tool", Content = "[]", ToolCallId = "list_devices:9f2a1c88" },
        };

        await Build(handler).ChatAsync(messages, "gemini-2.5-pro", 0.7, default);

        var fr = Assert.Single(Assert.Single(Contents(handler)).GetProperty("parts").EnumerateArray())
            .GetProperty("functionResponse");
        Assert.Equal("list_devices", fr.GetProperty("name").GetString());
    }

    // `response` must be an object. Tool handlers routinely return arrays or
    // bare text, which Gemini rejects unless wrapped.
    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("plain text")]
    [InlineData("42")]
    public async Task NonObjectToolResultsAreWrappedUnderResult(string content)
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new() { Role = "tool", Content = content, ToolCallId = "ping:1" },
        };

        await Build(handler).ChatAsync(messages, "gemini-2.5-pro", 0.7, default);

        var response = Assert.Single(Assert.Single(Contents(handler)).GetProperty("parts").EnumerateArray())
            .GetProperty("functionResponse").GetProperty("response");
        Assert.Equal(JsonValueKind.Object, response.ValueKind);
        Assert.True(response.TryGetProperty("result", out _));
    }

    // Parallel tool calls come back as one "tool" message each; Gemini wants
    // every response for a turn inside a single content.
    [Fact]
    public async Task ConsecutiveToolResultsAreMergedIntoOneTurn()
    {
        var handler = Empty();
        var messages = new List<LlmMessage>
        {
            new() { Role = "tool", Content = "{}", ToolCallId = "alpha:1" },
            new() { Role = "tool", Content = "{}", ToolCallId = "beta:2" },
        };

        await Build(handler).ChatAsync(messages, "gemini-2.5-pro", 0.7, default);

        var turn = Assert.Single(Contents(handler));
        Assert.Equal(2, turn.GetProperty("parts").GetArrayLength());
    }

    [Fact]
    public async Task ToolsAreWrappedInFunctionDeclarations()
    {
        var handler = Empty();
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "list_devices",
                Description = "Lists",
                ParametersSchema = TestJson.Element("""
                    {"type":"object","properties":{"limit":{"type":"integer"}},"required":["limit"]}
                    """),
            },
        };

        await Build(handler).ChatWithToolsAsync(UserSays(), tools, "gemini-2.5-pro", 0.7, default);

        var toolBlock = Assert.Single(SentBody(handler).GetProperty("tools").EnumerateArray());
        var decl = Assert.Single(toolBlock.GetProperty("functionDeclarations").EnumerateArray());
        Assert.Equal("list_devices", decl.GetProperty("name").GetString());
        Assert.Equal("OBJECT", decl.GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal("INTEGER",
            decl.GetProperty("parameters").GetProperty("properties").GetProperty("limit")
                .GetProperty("type").GetString());
    }

    // ─── schema sanitising ──────────────────────────────────────────────

    // Our tool schemas are written for OpenAI's stricter draft. Forwarding
    // `additionalProperties` (or any other unknown keyword) 400s the whole
    // request, taking every tool down with it.
    [Fact]
    public async Task UnsupportedSchemaKeywordsAreDropped()
    {
        var handler = Empty();
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "t",
                Description = "d",
                ParametersSchema = TestJson.Element("""
                    {"$schema":"https://json-schema.org/draft/2020-12/schema",
                     "type":"object",
                     "properties":{"q":{"type":"string","additionalProperties":false,"const":"x"}},
                     "additionalProperties":false}
                    """),
            },
        };

        await Build(handler).ChatWithToolsAsync(UserSays(), tools, "gemini-2.5-pro", 0.7, default);

        var raw = handler.RequestBodies[0];
        Assert.DoesNotContain("additionalProperties", raw);
        Assert.DoesNotContain("$schema", raw);
        Assert.DoesNotContain("\"const\"", raw);
    }

    // Supported constraints must survive the trip — dropping them silently
    // widens what the model may send.
    [Fact]
    public async Task SupportedSchemaKeywordsSurvive()
    {
        var handler = Empty();
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "t",
                Description = "d",
                ParametersSchema = TestJson.Element("""
                    {"type":"object","properties":{
                      "role":{"type":"string","enum":["admin","viewer"],"description":"Role"},
                      "limit":{"type":"integer","minimum":1,"maximum":100,"default":25},
                      "tags":{"type":"array","items":{"type":"string"}}},
                     "required":["role"]}
                    """),
            },
        };

        await Build(handler).ChatWithToolsAsync(UserSays(), tools, "gemini-2.5-pro", 0.7, default);

        var props = Assert.Single(
                Assert.Single(SentBody(handler).GetProperty("tools").EnumerateArray())
                    .GetProperty("functionDeclarations").EnumerateArray())
            .GetProperty("parameters");
        Assert.Equal("role", Assert.Single(props.GetProperty("required").EnumerateArray()).GetString());
        var role = props.GetProperty("properties").GetProperty("role");
        Assert.Equal(2, role.GetProperty("enum").GetArrayLength());
        Assert.Equal("Role", role.GetProperty("description").GetString());
        var limit = props.GetProperty("properties").GetProperty("limit");
        Assert.Equal(1, limit.GetProperty("minimum").GetInt32());
        Assert.Equal(25, limit.GetProperty("default").GetInt32());
        Assert.Equal("STRING",
            props.GetProperty("properties").GetProperty("tags").GetProperty("items")
                .GetProperty("type").GetString());
    }

    // A ["string","null"] union has no Gemini equivalent; it collapses to the
    // concrete type plus `nullable`.
    [Fact]
    public async Task NullableUnionTypesCollapseToNullable()
    {
        var handler = Empty();
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "t",
                Description = "d",
                ParametersSchema = TestJson.Element("""
                    {"type":"object","properties":{"note":{"type":["string","null"]}}}
                    """),
            },
        };

        await Build(handler).ChatWithToolsAsync(UserSays(), tools, "gemini-2.5-pro", 0.7, default);

        var note = Assert.Single(
                Assert.Single(SentBody(handler).GetProperty("tools").EnumerateArray())
                    .GetProperty("functionDeclarations").EnumerateArray())
            .GetProperty("parameters").GetProperty("properties").GetProperty("note");
        Assert.Equal("STRING", note.GetProperty("type").GetString());
        Assert.True(note.GetProperty("nullable").GetBoolean());
    }

    // An OBJECT schema with no properties is rejected outright; a tool that
    // takes no arguments has to declare no `parameters` at all.
    [Fact]
    public async Task ToolWithAnEmptySchemaSendsNoParameters()
    {
        var handler = Empty();
        var tools = new List<ToolDefinition>
        {
            new() { Name = "ping", Description = "Pings", ParametersSchema = EmptySchema },
        };

        await Build(handler).ChatWithToolsAsync(UserSays(), tools, "gemini-2.5-pro", 0.7, default);

        var decl = Assert.Single(
            Assert.Single(SentBody(handler).GetProperty("tools").EnumerateArray())
                .GetProperty("functionDeclarations").EnumerateArray());
        Assert.False(decl.TryGetProperty("parameters", out _));
    }

    [Fact]
    public async Task NoToolsMeansNoToolsField()
    {
        var handler = Empty();

        await Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default);

        Assert.False(SentBody(handler).TryGetProperty("tools", out _));
    }

    // ─── response parsing ───────────────────────────────────────────────

    [Fact]
    public async Task ConcatenatesMultipleTextPartsAndReadsUsage()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"candidates":[{"content":{"role":"model","parts":[{"text":"Hello "},{"text":"world"}]},
              "finishReason":"STOP"}],
             "usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":2}}
            """);

        var result = await Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default);

        Assert.Equal("Hello world", result.Content);
        Assert.Equal(10, result.InputTokens);
        Assert.Equal(2, result.OutputTokens);
        Assert.Equal("STOP", result.StopReason);
    }

    // Gemini supplies no call id, so the provider mints one prefixed with the
    // function name (the pairing key on the way back).
    [Fact]
    public async Task ParsesFunctionCallsAndSynthesizesANamePrefixedId()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"candidates":[{"content":{"role":"model","parts":[
              {"functionCall":{"name":"list_devices","args":{"limit":5}}}]},"finishReason":"STOP"}],
             "usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1}}
            """);

        var result = await Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default);

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("list_devices", call.Name);
        Assert.Equal(5, call.Arguments.GetProperty("limit").GetInt32());
        Assert.StartsWith("list_devices:", call.Id);
    }

    // A functionCall with no args must still expose an object, not Undefined.
    [Fact]
    public async Task FunctionCallWithoutArgsYieldsEmptyArguments()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"ping"}}]}}]}
            """);

        var result = await Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default);

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal(JsonValueKind.Object, call.Arguments.ValueKind);
        Assert.Empty(call.Arguments.EnumerateObject());
    }

    // Thought summaries share the parts array with the answer but are not reply
    // text — surfacing them would leak reasoning into the chat transcript.
    [Fact]
    public async Task ThoughtPartsAreNotTreatedAsReplyText()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """
            {"candidates":[{"content":{"role":"model","parts":[
              {"text":"internal reasoning","thought":true},{"text":"answer"}]}}]}
            """);

        var result = await Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default);

        Assert.Equal("answer", result.Content);
    }

    [Theory]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{}""")]
    [InlineData("""{"candidates":null,"usageMetadata":null}""")]
    [InlineData("""{"candidates":[{"content":{}}]}""")]
    [InlineData("""{"candidates":[{"content":{"parts":[{}]}}]}""")]
    public async Task DegenerateResponsesDegradeToEmptyInsteadOfThrowing(string json)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, json);

        var result = await Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default);

        Assert.NotNull(result.Content);
        Assert.Null(result.ToolCalls);
        Assert.Equal(0, result.InputTokens);
        Assert.Equal(0, result.OutputTokens);
    }

    // Fatal codes only — the transient ones go through the retry layer and are
    // covered under "retry policy" below.
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task UpstreamErrorsThrow(int status)
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode)status, """{"error":{"message":"nope"}}""");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default));

        Assert.Equal((HttpStatusCode)status, ex.StatusCode);
    }

    // The upstream reason has to travel with the exception: EnsureSuccessStatusCode
    // reports only "Response status code does not indicate success: 404", which
    // leaves the chat surface and the Test button with nothing to act on. A wrong
    // model id is the failure this exists for.
    [Fact]
    public async Task UpstreamErrorMessageIsSurfacedInTheException()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.NotFound, """
            {"error":{"code":404,
              "message":"models/gemini-3.1-pro is not found for API version v1beta, or is not supported for generateContent.",
              "status":"NOT_FOUND"}}
            """);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).ChatAsync(UserSays(), "gemini-3.1-pro", 0.7, default));

        Assert.Contains("gemini 404", ex.Message);
        Assert.Contains("models/gemini-3.1-pro is not found", ex.Message);
    }

    // Same requirement on the streaming path, which is what the chat surface uses.
    [Fact]
    public async Task StreamingSurfacesTheUpstreamErrorMessage()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.BadRequest, """
            {"error":{"code":400,"message":"Invalid JSON payload received. Unknown name \"additionalProperties\".","status":"INVALID_ARGUMENT"}}
            """);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Drain(Build(handler)));

        Assert.Contains("Unknown name", ex.Message);
    }

    // A body that isn't Gemini's error envelope (proxy HTML, empty response) must
    // still yield something rather than an empty message.
    [Theory]
    [InlineData("<html><body>502 Bad Gateway</body></html>", "502 Bad Gateway")]
    [InlineData("""{"error":{"code":403}}""", "403")]
    [InlineData("", "(empty)")]
    public async Task NonEnvelopeErrorBodiesFallBackToTheRawText(string body, string expected)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.Forbidden, body);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default));

        Assert.Contains(expected, ex.Message);
    }

    // A pathological error page must not end up verbatim in the exception, the
    // trace and the toast.
    [Fact]
    public async Task LongErrorBodiesAreTruncated()
    {
        var handler = new FakeHttpMessageHandler(
            HttpStatusCode.BadRequest, new string('x', 5000));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).ChatAsync(UserSays(), "gemini-2.5-pro", 0.7, default));

        Assert.True(ex.Message.Length < 500, $"message was {ex.Message.Length} chars");
        Assert.EndsWith("…", ex.Message);
    }

    // ─── SSE streaming ──────────────────────────────────────────────────

    [Fact]
    public async Task StreamsTextDeltas()
    {
        var handler = Sse(
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"Hel"}]}}]}""",
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"lo"}]}}]}""");

        var events = await Drain(Build(handler));

        Assert.Equal(new[] { "Hel", "lo" },
            events.Where(e => e.Type == "text_delta").Select(e => e.TextDelta));
    }

    // Gemini never splits a functionCall across chunks — it arrives whole.
    [Fact]
    public async Task StreamsWholeFunctionCalls()
    {
        var handler = Sse(
            """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"ping","args":{"host":"a"}}}]}}]}""",
            """{"candidates":[{"content":{"role":"model","parts":[]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":1}}""");

        var events = await Drain(Build(handler));

        var call = Assert.Single(events, e => e.Type == "tool_call").ToolCall!;
        Assert.Equal("ping", call.Name);
        Assert.Equal("a", call.Arguments.GetProperty("host").GetString());
    }

    // The SSE body just ends — there is no [DONE] and no terminating event, so
    // the `done` (and its token counts) has to be synthesised on close.
    [Fact]
    public async Task SynthesisesADoneEventCarryingTheLastUsageMetadata()
    {
        var handler = Sse(
            """{"candidates":[{"content":{"parts":[{"text":"hi"}]}}],"usageMetadata":{"promptTokenCount":9,"candidatesTokenCount":1}}""",
            """{"candidates":[{"content":{"parts":[{"text":"!"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":9,"candidatesTokenCount":4}}""");

        var events = await Drain(Build(handler));

        var done = Assert.Single(events, e => e.Type == "done");
        Assert.Same(done, events[^1]);
        Assert.Equal(9, done.InputTokens);
        Assert.Equal(4, done.OutputTokens);
    }

    [Fact]
    public async Task StreamWithNoUsageMetadataStillEmitsDone()
    {
        var handler = Sse("""{"candidates":[{"content":{"parts":[{"text":"hi"}]}}]}""");

        var events = await Drain(Build(handler));

        var done = Assert.Single(events, e => e.Type == "done");
        Assert.Equal(0, done.InputTokens);
        Assert.Equal(0, done.OutputTokens);
    }

    [Fact]
    public async Task StreamedThoughtPartsAreNotEmittedAsText()
    {
        var handler = Sse(
            """{"candidates":[{"content":{"parts":[{"text":"reasoning","thought":true}]}}]}""",
            """{"candidates":[{"content":{"parts":[{"text":"answer"}]}}]}""");

        var events = await Drain(Build(handler));

        Assert.Equal("answer", Assert.Single(events, e => e.Type == "text_delta").TextDelta);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"candidates":[]}""")]
    [InlineData("""{"candidates":[{"content":null}]}""")]
    [InlineData("""{"promptFeedback":{"blockReason":"SAFETY"}}""")]
    public async Task SurvivesUnknownOrMalformedChunks(string chunk)
    {
        var handler = Sse(chunk,
            """{"candidates":[{"content":{"parts":[{"text":"ok"}]}}]}""");

        var events = await Drain(Build(handler));

        Assert.Contains(events, e => e.Type == "text_delta" && e.TextDelta == "ok");
        Assert.Equal("done", events[^1].Type);
    }

    // ─── retry policy ───────────────────────────────────────────────────

    private static HttpResponseMessage Reply(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string OkBody = """
        {"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]}}],
         "usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1}}
        """;

    // A 503 UNAVAILABLE under load is the failure this exists for: one transient
    // answer used to kill the whole agent turn.
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
                ? Reply((HttpStatusCode)status, """{"error":{"code":503,"message":"high demand"}}""")
                : Reply(HttpStatusCode.OK, OkBody);
        });

        var result = await Build(handler).ChatAsync(UserSays(), "gemini-3.5-flash", 0.7, default);

        Assert.Equal(2, attempts);
        Assert.Equal("ok", result.Content);
    }

    // The streaming path retries too — it must, since that is what the chat
    // surface uses. Safe because nothing has been yielded yet.
    [Fact]
    public async Task StreamingRetriesTransientStatusCodes()
    {
        var attempts = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return attempts == 1
                ? Reply(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"high demand"}}""")
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """data: {"candidates":[{"content":{"parts":[{"text":"ok"}]}}]}""" + "\n\n",
                        Encoding.UTF8, "text/event-stream"),
                };
        });

        var events = await Drain(Build(handler));

        Assert.Equal(2, attempts);
        Assert.Contains(events, e => e.Type == "text_delta" && e.TextDelta == "ok");
    }

    // Retrying a rejected schema or an unknown model id repeats the same answer.
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
            return Reply((HttpStatusCode)status, """{"error":{"message":"bad"}}""");
        });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).ChatAsync(UserSays(), "gemini-3.5-flash", 0.7, default));

        Assert.Equal(1, attempts);
    }

    // After the backoff schedule is exhausted (3 retries → 4 attempts) the
    // upstream reason still surfaces instead of looping forever.
    [Fact]
    public async Task GivesUpAfterTheRetryBudgetIsExhausted()
    {
        var attempts = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            return Reply(HttpStatusCode.ServiceUnavailable, """
                {"error":{"code":503,"message":"This model is currently experiencing high demand.","status":"UNAVAILABLE"}}
                """);
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Build(handler).ChatAsync(UserSays(), "gemini-3.5-flash", 0.7, default));

        Assert.Equal(4, attempts);
        Assert.Contains("high demand", ex.Message);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    // A socket reset means no status code was observed, so the request may never
    // have landed — same treatment as a 503.
    [Fact]
    public async Task RetriesNetworkFailures()
    {
        var attempts = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            attempts++;
            if (attempts == 1) throw new HttpRequestException("connection reset");
            return Reply(HttpStatusCode.OK, OkBody);
        });

        var result = await Build(handler).ChatAsync(UserSays(), "gemini-3.5-flash", 0.7, default);

        Assert.Equal(2, attempts);
        Assert.Equal("ok", result.Content);
    }

    [Fact]
    public void ProviderTypeIsGemini()
        => Assert.Equal("gemini", Build(Empty()).ProviderType);
}
