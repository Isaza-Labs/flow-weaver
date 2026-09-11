using System.Net;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services;
using flow_weaver_backend.Services.Ai;
using flow_weaver_backend.Services.Ai.Conversation;
using flow_weaver_backend.Services.Ai.Permissions;
using flow_weaver_backend.Services.Ai.Providers;
using flow_weaver_backend.Services.Ai.Skills;
using ApiOperation = flow_weaver_backend.Dtos.ApiOperation;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Ai.Tools;
using flow_weaver_backend.Services.Observability;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AiAgentModel = flow_weaver_backend.Models.AIAgent;

namespace flow_weaver_backend.Tests;

// The agent loop itself: stream → tool calls → feed results back → stream
// again, until the model stops asking for tools or a limit trips. It is driven
// end-to-end here through a REAL OpenAiProvider fed by a scripted SSE stream,
// so the message-shaping and loop-control logic are exercised together.
//
// The invariants that matter: the loop must always terminate (iteration cap,
// deadline), the turn must always be persisted (even on timeout or error), and
// a failure must come back as a structured result rather than an exception —
// an SSE caller has already committed to a reader and cannot re-parse a throw.
public class AgentConversationRunnerTests
{
    private static readonly Guid User = new("22222222-2222-2222-2222-222222222222");

    // Captures every sink event so ordering and payloads are assertable.
    private sealed class RecordingSink : IAgentEventSink
    {
        public List<string> Events { get; } = new();
        public StringBuilder Text { get; } = new();
        public List<string> ToolStarts { get; } = new();
        public List<(string Tool, bool Success)> ToolResults { get; } = new();
        public (Guid Id, bool IsNew)? Conversation { get; private set; }
        public (int In, int Out, int Iterations)? Done { get; private set; }
        public (string Tail, int Deadline)? Timeout { get; private set; }
        public (string Message, string? Code)? Error { get; private set; }

        public Task ConversationAsync(Guid conversationId, bool isNew, CancellationToken ct)
        {
            Conversation = (conversationId, isNew);
            Events.Add("conversation");
            return Task.CompletedTask;
        }
        public Task TextAsync(string delta, CancellationToken ct)
        {
            Text.Append(delta);
            Events.Add("text");
            return Task.CompletedTask;
        }
        public Task ToolStartAsync(string toolName, JsonElement arguments, CancellationToken ct)
        {
            ToolStarts.Add(toolName);
            Events.Add("tool_start");
            return Task.CompletedTask;
        }
        public Task ToolResultAsync(string toolName, ToolCallOutput output, CancellationToken ct)
        {
            ToolResults.Add((toolName, output.Success));
            Events.Add("tool_result");
            return Task.CompletedTask;
        }
        public Task DoneAsync(int tokensIn, int tokensOut, int iterations, CancellationToken ct)
        {
            Done = (tokensIn, tokensOut, iterations);
            Events.Add("done");
            return Task.CompletedTask;
        }
        public Task TimeoutAsync(string partialTail, int deadlineSeconds, CancellationToken ct)
        {
            Timeout = (partialTail, deadlineSeconds);
            Events.Add("timeout");
            return Task.CompletedTask;
        }
        public Task ErrorAsync(string message, string? code, CancellationToken ct)
        {
            Error = (message, code);
            Events.Add("error");
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler : IToolHandler
    {
        public string Name { get; init; } = "list_workflows";
        public string Description => "Lists workflows";
        public JsonElement ParametersSchema => TestJson.Element("""{"type":"object"}""");
        public int Calls { get; private set; }
        public JsonElement Result { get; set; } = TestJson.Element("""{"workflows":[]}""");
        public Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class StubEffectivePermissions : IEffectivePermissions
    {
        public Task<bool> HasAsync(string c, PermissionContext ctx, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> HasAsync(string c, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    // Each HTTP call to the LLM returns the next scripted SSE stream, so a
    // multi-iteration loop can be driven deterministically.
    private sealed class ScriptedLlm
    {
        private readonly Queue<string> _responses;
        public int Calls { get; private set; }
        public List<string> RequestBodies { get; } = new();

        public ScriptedLlm(params string[] sseResponses)
            => _responses = new Queue<string>(sseResponses);

        public FakeHttpMessageHandler Handler => new(req =>
        {
            Calls++;
            RequestBodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var body = _responses.Count > 0 ? _responses.Dequeue() : Sse(Done());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            };
        });
    }

    private static string Sse(params string[] frames)
    {
        var sb = new StringBuilder();
        foreach (var frame in frames) sb.Append("data: ").Append(frame).Append("\n\n");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    // Built by concatenation rather than raw interpolated literals: the JSON
    // braces collide with `{{`/`}}` escaping and make these unreadable.
    private static string TextFrame(string text)
        => "{\"choices\":[{\"delta\":{\"content\":" + JsonSerializer.Serialize(text) + "}}]}";

    private static string ToolFrame(string id, string name, string argsJson)
        => "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":"
           + JsonSerializer.Serialize(id)
           + ",\"function\":{\"name\":" + JsonSerializer.Serialize(name)
           + ",\"arguments\":" + JsonSerializer.Serialize(argsJson) + "}}]}}]}";

    private static string Done(int inTokens = 10, int outTokens = 5)
        => "{\"choices\":[],\"usage\":{\"prompt_tokens\":" + inTokens
           + ",\"completion_tokens\":" + outTokens + "}}";

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public ToolRegistry Registry { get; } = new(NullLogger<ToolRegistry>.Instance);
        public List<StubHandler> Handlers { get; } = new();
        public ScriptedLlm Llm { get; set; } = new();
        public Guid ProviderId { get; private set; }
        // Operation id → api, for the scoped-skill auto-load path. Empty by default.
        public Dictionary<string, string> OperationApis { get; } = new();

        public StubHandler RegisterTool(string name)
        {
            var h = new StubHandler { Name = name };
            Handlers.Add(h);
            Registry.Register(h);
            return h;
        }

        public Guid SeedProvider(bool enabled = true)
        {
            ProviderId = Guid.NewGuid();
            Db.AIProviders.Add(new AIProvider
            {
                AIProviderId = ProviderId,
                Name = "openai-prod",
                Type = "openai",
                BaseURL = "https://api.openai.test",
                EncryptedApiKey = Encoding.UTF8.GetBytes("sk-test"),
                DefaultModel = "gpt-4o",
                Enabled = enabled,
                IsActive = true,
            });
            Db.SaveChanges();
            return ProviderId;
        }

        public Guid SeedAgent(
            string role = "assistant", bool enabled = true, int maxIterations = 20,
            List<string>? tools = null, string systemPrompt = "You are helpful.",
            Guid? providerId = null)
        {
            var id = Guid.NewGuid();
            Db.AIAgents.Add(new AiAgentModel
            {
                AIAgentId = id,
                Name = "assistant",
                Role = role,
                ProviderId = providerId ?? ProviderId,
                SystemPrompt = systemPrompt,
                Tools = tools ?? new List<string>(),
                MaxIterations = maxIterations,
                Temperature = 0.2,
                Enabled = enabled,
                IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public AgentConversationRunner Build(AiChatOptions? options = null)
        {
            var caller = new FakeUser();
            var services = new ServiceCollection();
            foreach (var h in Handlers) services.AddSingleton(h.GetType(), h);
            var sp = services.BuildServiceProvider();

            var factory = new LlmProviderFactory(
                new AiProviderRepository(Db),
                new RepositoryBase<AiAgentModel>(Db),
                caller,
                new FakeHttpClientFactory(Llm.Handler),
                new FakeCrypto(),
                NullLoggerFactory.Instance);

            var dispatcher = new ToolDispatcher(
                Registry, sp, caller, new FakeTrace(),
                new PermissionClassifier(NullLogger<PermissionClassifier>.Instance),
                new StubEffectivePermissions(),
                new FakeAppSettings(),
                NullLogger<ToolDispatcher>.Instance);

            return new AgentConversationRunner(
                Db,
                new AIConversationRepository(Db),
                new AgentRunRepository(Db),
                factory,
                Registry,
                dispatcher,
                new FakeSkillPromptLoader(),
                new ScopedSkillCatalog(Db, new MappingSpecIndex(OperationApis)),
                caller,
                new FakeTrace(),
                new ToolExecutionContext(),
                Options.Create(options ?? new AiChatOptions { StreamDeadlineSeconds = 30 }),
                NullLogger<AgentConversationRunner>.Instance);
        }

        public void Dispose() => Db.Dispose();
    }

    private sealed class MappingSpecIndex(Dictionary<string, string> opToApi) : IApiSpecIndex
    {
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IReadOnlyList<ApiOperation> All() => [];
        public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null) => [];
        public ApiOperation? GetByOperationId(string operationId)
            => opToApi.TryGetValue(operationId, out var api) ? new ApiOperation { OperationId = operationId, Api = api } : null;
    }

    // Seeds an integration with one scoped skill and one spec bound to it.
    private static Guid SeedScopedSkill(Fixture f, string slug, string skillText, string api, string operationId)
    {
        var integrationId = Guid.NewGuid();
        f.Db.Integrations.Add(new Integration { IntegrationId = integrationId, Name = slug.ToUpperInvariant(), Slug = slug, Type = "http", IsActive = true });
        f.Db.AiPromptSkills.Add(new AiPromptSkill { AiPromptSkillId = Guid.NewGuid(), Name = slug + "-skill", Content = skillText, IntegrationId = integrationId, IsActive = true });
        f.Db.AiApiSpecs.Add(new AiApiSpec { AiApiSpecId = Guid.NewGuid(), Api = api, Content = "openapi: 3.0.0", IntegrationId = integrationId, IsActive = true });
        f.Db.SaveChanges();
        f.OperationApis[operationId] = api;
        return integrationId;
    }

    private static string SystemPromptOf(Fixture f, int call)
        => TestJson.Element(f.Llm.RequestBodies[call]).GetProperty("messages")[0].GetProperty("content").GetString()!;

    private static AgentTurnRequest Turn(
        string message = "hello", Guid? conversationId = null, Guid? agentId = null, int? deadline = null)
        => new()
        {
            Message = message,
            ConversationId = conversationId,
            AgentId = agentId,
            DeadlineSecondsOverride = deadline,
        };

    // ─── agent / provider resolution ────────────────────────────────────

    // A missing provider must be an actionable error, not an obscure upstream
    // 400 later in the turn.
    [Fact]
    public async Task NoProviderConfigured_ReportsThroughTheSink()
    {
        using var f = new Fixture();
        var sink = new RecordingSink();

        var result = await f.Build().RunAsync(Turn(), sink, default);

        Assert.NotNull(result.Error);
        Assert.Contains("No AI provider", result.Error);
        Assert.Equal("no_provider", sink.Error!.Value.Code);
    }

    [Fact]
    public async Task ProviderButNoAgent_ReportsNoAgent()
    {
        using var f = new Fixture();
        f.SeedProvider();
        var sink = new RecordingSink();

        var result = await f.Build().RunAsync(Turn(), sink, default);

        Assert.Contains("No AI agent", result.Error);
        Assert.Equal("no_agent", sink.Error!.Value.Code);
    }

    // Only an enabled "assistant" agent is picked as the implicit default.
    [Fact]
    public async Task DisabledDefaultAgentIsNotSelected()
    {
        using var f = new Fixture();
        f.SeedProvider();
        f.SeedAgent(enabled: false);
        var sink = new RecordingSink();

        var result = await f.Build().RunAsync(Turn(), sink, default);

        Assert.Contains("No AI agent", result.Error);
    }

    [Fact]
    public async Task NonAssistantRoleIsNotSelectedAsDefault()
    {
        using var f = new Fixture();
        f.SeedProvider();
        f.SeedAgent(role: "reviewer");
        var sink = new RecordingSink();

        Assert.Contains("No AI agent", (await f.Build().RunAsync(Turn(), sink, default)).Error);
    }

    // A resolution failure must never throw out of RunAsync.
    [Fact]
    public async Task UnknownExplicitAgentIsAStructuredErrorNotAThrow()
    {
        using var f = new Fixture();
        f.SeedProvider();
        var sink = new RecordingSink();

        var result = await f.Build().RunAsync(Turn(agentId: Guid.NewGuid()), sink, default);

        Assert.NotNull(result.Error);
        Assert.NotNull(sink.Error);
    }

    // ─── the happy path ─────────────────────────────────────────────────

    private static Fixture ReadyFixture(params string[] sseResponses)
    {
        var f = new Fixture();
        f.SeedProvider();
        f.SeedAgent();
        f.Llm = new ScriptedLlm(sseResponses);
        return f;
    }

    [Fact]
    public async Task SingleTurnStreamsTextAndCompletes()
    {
        using var f = ReadyFixture(Sse(TextFrame("Hello "), TextFrame("there"), Done(7, 3)));
        var sink = new RecordingSink();

        var result = await f.Build().RunAsync(Turn(), sink, default);

        Assert.Null(result.Error);
        Assert.Equal("Hello there", result.FinalText);
        Assert.Equal("Hello there", sink.Text.ToString());
        Assert.Equal(1, result.Iterations);
        Assert.Equal(7, result.TokensIn);
        Assert.Equal(3, result.TokensOut);
        Assert.False(result.TimedOut);
    }

    // The conversation id is echoed immediately so a client that sent null can
    // learn it — otherwise every follow-up starts a new thread.
    [Fact]
    public async Task ConversationIdIsAnnouncedBeforeAnyText()
    {
        using var f = ReadyFixture(Sse(TextFrame("hi"), Done()));
        var sink = new RecordingSink();

        var result = await f.Build().RunAsync(Turn(), sink, default);

        Assert.Equal("conversation", sink.Events[0]);
        Assert.True(sink.Conversation!.Value.IsNew);
        Assert.Equal(result.ConversationId, sink.Conversation.Value.Id);
        Assert.NotEqual(Guid.Empty, result.ConversationId);
    }

    [Fact]
    public async Task ExistingConversationIdIsReusedAndNotMarkedNew()
    {
        using var f = ReadyFixture(Sse(TextFrame("hi"), Done()));
        var conversationId = Guid.NewGuid();
        var sink = new RecordingSink();

        var result = await f.Build().RunAsync(Turn(conversationId: conversationId), sink, default);

        Assert.Equal(conversationId, result.ConversationId);
        Assert.False(result.IsNewConversation);
        Assert.False(sink.Conversation!.Value.IsNew);
    }

    [Fact]
    public async Task DoneEventCarriesTokensAndIterations()
    {
        using var f = ReadyFixture(Sse(TextFrame("hi"), Done(11, 4)));
        var sink = new RecordingSink();

        await f.Build().RunAsync(Turn(), sink, default);

        Assert.Equal((11, 4, 1), sink.Done);
    }

    // ─── the tool loop ──────────────────────────────────────────────────

    // One full cycle: the model asks for a tool, the result is fed back, and a
    // second stream produces the final answer.
    [Fact]
    public async Task ToolCallIsDispatchedAndTheLoopContinues()
    {
        using var f = ReadyFixture(
            Sse(ToolFrame("call_1", "list_workflows", """{"limit":5}"""), Done(5, 2)),
            Sse(TextFrame("You have 3 workflows."), Done(6, 4)));
        var handler = f.RegisterTool("list_workflows");
        var sink = new RecordingSink();

        var result = await f.Build().RunAsync(Turn(), sink, default);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(2, result.Iterations);
        Assert.Equal("You have 3 workflows.", result.FinalText);
        Assert.Equal("list_workflows", Assert.Single(sink.ToolStarts));
        Assert.Equal(("list_workflows", true), Assert.Single(sink.ToolResults));
    }

    [Fact]
    public async Task ToolCallIsReportedInTheResult()
    {
        using var f = ReadyFixture(
            Sse(ToolFrame("call_1", "list_workflows", "{}"), Done()),
            Sse(TextFrame("done"), Done()));
        f.RegisterTool("list_workflows");
        var sink = new RecordingSink();

        var result = await f.Build().RunAsync(Turn(), sink, default);

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("list_workflows", call.Name);
        Assert.True(call.Success);
    }

    // Token counts accumulate across every iteration — billing must cover the
    // whole turn, not just the last stream.
    [Fact]
    public async Task TokensAccumulateAcrossIterations()
    {
        using var f = ReadyFixture(
            Sse(ToolFrame("call_1", "list_workflows", "{}"), Done(10, 5)),
            Sse(TextFrame("ok"), Done(20, 7)));
        f.RegisterTool("list_workflows");

        var result = await f.Build().RunAsync(Turn(), new RecordingSink(), default);

        Assert.Equal(30, result.TokensIn);
        Assert.Equal(12, result.TokensOut);
    }

    // A stream that ends with finish_reason "length" is an unfinished answer. The
    // runner appends a notice the user can read, persists it with the text, flags
    // the result, and does not go round again.
    [Fact]
    public async Task OutputCutOffAppendsANoticeAndEndsTheTurn()
    {
        using var f = ReadyFixture(Sse(
            TextFrame("Half an ans"),
            """{"choices":[{"delta":{},"finish_reason":"length"}]}""",
            Done()));
        var sink = new RecordingSink();

        var result = await f.Build().RunAsync(Turn(), sink, default);

        Assert.True(result.OutputCutOff);
        Assert.False(result.TimedOut);
        Assert.Null(result.Error);
        Assert.StartsWith("Half an ans", result.FinalText);
        Assert.EndsWith(AgentConversationRunner.CutOffNotice, result.FinalText);
        Assert.Equal(1, f.Llm.Calls);

        var conv = await f.Db.AIConversations.SingleAsync();
        var last = conv.Messages.EnumerateArray().Last().GetProperty("content").GetString();
        Assert.EndsWith(AgentConversationRunner.CutOffNotice, last);
    }

    // A tool call the model was mid-way through when it ran out of tokens is not
    // dispatched: the turn ends on the notice, the tool is never invoked.
    [Fact]
    public async Task OutputCutOffDoesNotDispatchPendingToolCalls()
    {
        using var f = ReadyFixture(Sse(
            ToolFrame("call_1", "list_workflows", "{}"),
            """{"choices":[{"delta":{},"finish_reason":"length"}]}""",
            Done()));
        var handler = f.RegisterTool("list_workflows");

        var result = await f.Build().RunAsync(Turn(), new RecordingSink(), default);

        Assert.True(result.OutputCutOff);
        Assert.Equal(0, handler.Calls);
        Assert.Empty(result.ToolCalls);
    }

    // ─── integration skills ─────────────────────────────────────────────

    // A scoped skill is indexed in every turn and absent from the prompt until
    // its integration is in play. Naming the integration in the message is one way.
    [Fact]
    public async Task ScopedSkillIsIndexedButNotLoadedUntilMentioned()
    {
        using var f = ReadyFixture(Sse(TextFrame("ok"), Done()), Sse(TextFrame("ok"), Done()));
        SeedScopedSkill(f, "action1", "# Action1 rules\nACTION1 SKILL TEXT", "action1", "action1:endpoints_list");

        await f.Build().RunAsync(Turn("how many workflows do I have"), new RecordingSink(), default);
        var quiet = SystemPromptOf(f, 0);
        Assert.Contains("## Integration skills", quiet);
        Assert.Contains("`action1-skill`", quiet);
        Assert.DoesNotContain("ACTION1 SKILL TEXT", quiet);

        await f.Build().RunAsync(Turn("list the Action1 endpoints"), new RecordingSink(), default);
        var loaded = SystemPromptOf(f, 1);
        Assert.Contains("ACTION1 SKILL TEXT", loaded);
        Assert.Contains("loaded — its text follows below", loaded);
    }

    // A call to the integration's API loads its skill mid-turn, right behind the
    // tool result, and the load is remembered by the conversation: the next turn
    // starts with the skill already in the system prompt.
    [Fact]
    public async Task ApiCallAutoLoadsTheSkillAndTheConversationRemembersIt()
    {
        using var f = ReadyFixture(
            Sse(ToolFrame("call_1", "execute_operation", """{"operation_id":"action1:endpoints_list"}"""), Done()),
            Sse(TextFrame("done"), Done()),
            Sse(TextFrame("again"), Done()));
        f.RegisterTool("execute_operation");
        SeedScopedSkill(f, "action1", "# Action1 rules\nACTION1 SKILL TEXT", "action1", "action1:endpoints_list");

        var first = await f.Build().RunAsync(Turn("show me the endpoints"), new RecordingSink(), default);

        Assert.DoesNotContain("ACTION1 SKILL TEXT", SystemPromptOf(f, 0));
        var afterTool = TestJson.Element(f.Llm.RequestBodies[1]).GetProperty("messages").EnumerateArray().ToList();
        var injected = afterTool.Last(m => m.GetProperty("role").GetString() == "system");
        Assert.Contains("ACTION1 SKILL TEXT", injected.GetProperty("content").GetString());

        var conv = await f.Db.AIConversations.SingleAsync();
        Assert.Single(ScopedSkillCatalog.LoadedFromContext(conv.Context));

        await f.Build().RunAsync(Turn("and now?", conversationId: first.ConversationId), new RecordingSink(), default);
        Assert.Contains("ACTION1 SKILL TEXT", SystemPromptOf(f, 2));
    }

    // An agent with an explicit tool list that predates load_skill still gets it
    // when scoped skills exist — it is how the model reaches a skill nobody named.
    [Fact]
    public async Task LoadSkillToolIsOfferedEvenWhenTheAgentToolListOmitsIt()
    {
        using var f = new Fixture();
        f.SeedProvider();
        f.SeedAgent(tools: new List<string> { "list_workflows" });
        f.RegisterTool("list_workflows");
        var loadSkill = f.RegisterTool("load_skill");
        SeedScopedSkill(f, "action1", "# Action1 rules\nACTION1 SKILL TEXT", "action1", "action1:endpoints_list");
        f.Llm = new ScriptedLlm(Sse(TextFrame("ok"), Done()));

        await f.Build().RunAsync(Turn("hello"), new RecordingSink(), default);

        var offered = TestJson.Element(f.Llm.RequestBodies[0]).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToList();
        Assert.Contains("load_skill", offered);
        Assert.Contains("list_workflows", offered);
        Assert.Equal(0, loadSkill.Calls);
    }

    // The tool result is fed back to the model as a `tool` message keyed by the
    // call id — without it the next request is malformed.
    [Fact]
    public async Task ToolResultIsFedBackAsAToolMessage()
    {
        using var f = ReadyFixture(
            Sse(ToolFrame("call_1", "list_workflows", "{}"), Done()),
            Sse(TextFrame("ok"), Done()));
        var handler = f.RegisterTool("list_workflows");
        handler.Result = TestJson.Element("""{"count":3}""");

        await f.Build().RunAsync(Turn(), new RecordingSink(), default);

        // Second request carries assistant(tool_calls) + tool(result).
        var second = TestJson.Element(f.Llm.RequestBodies[1]);
        var messages = second.GetProperty("messages").EnumerateArray().ToList();
        var toolMsg = messages.Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Equal("call_1", toolMsg.GetProperty("tool_call_id").GetString());
        Assert.Contains("\"count\":3", toolMsg.GetProperty("content").GetString());
    }

    // A denied tool still feeds an error result back so the model can recover
    // instead of stalling.
    [Fact]
    public async Task DeniedToolStillFeedsAResultBack()
    {
        using var f = ReadyFixture(
            Sse(ToolFrame("call_1", "unknown_tool", "{}"), Done()),
            Sse(TextFrame("I could not do that."), Done()));
        var sink = new RecordingSink();

        var result = await f.Build().RunAsync(Turn(), sink, default);

        Assert.Equal(("unknown_tool", false), Assert.Single(sink.ToolResults));
        Assert.False(Assert.Single(result.ToolCalls).Success);
        Assert.Equal("I could not do that.", result.FinalText);
    }

    // Only the agent's allow-listed tools are offered to the model.
    [Fact]
    public async Task AgentToolAllowlistFiltersTheOfferedTools()
    {
        using var f = new Fixture();
        f.SeedProvider();
        f.SeedAgent(tools: new List<string> { "list_workflows" });
        f.RegisterTool("list_workflows");
        f.RegisterTool("delete_device");
        f.Llm = new ScriptedLlm(Sse(TextFrame("hi"), Done()));

        await f.Build().RunAsync(Turn(), new RecordingSink(), default);

        var tools = TestJson.Element(f.Llm.RequestBodies[0]).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "list_workflows" }, tools);
    }

    // ─── loop termination ───────────────────────────────────────────────

    // The iteration cap is the backstop against a model that keeps asking for
    // tools forever.
    [Fact]
    public async Task IterationCapStopsAnEndlessToolLoop()
    {
        var alwaysCallsATool = Enumerable.Range(0, 10)
            .Select(_ => Sse(ToolFrame("call_x", "list_workflows", "{}"), Done()))
            .ToArray();
        using var f = new Fixture();
        f.SeedProvider();
        f.SeedAgent(maxIterations: 3);
        f.RegisterTool("list_workflows");
        f.Llm = new ScriptedLlm(alwaysCallsATool);

        var result = await f.Build().RunAsync(Turn(), new RecordingSink(), default);

        Assert.Equal(3, result.Iterations);
        Assert.Equal(3, f.Llm.Calls);
    }

    // A stream with no tool calls ends the turn immediately.
    [Fact]
    public async Task NoToolCallsEndsTheTurnAfterOneIteration()
    {
        using var f = ReadyFixture(Sse(TextFrame("just an answer"), Done()));

        var result = await f.Build().RunAsync(Turn(), new RecordingSink(), default);

        Assert.Equal(1, result.Iterations);
        Assert.Equal(1, f.Llm.Calls);
    }

    // ─── persistence ────────────────────────────────────────────────────

    [Fact]
    public async Task TurnIsPersistedAsUserAndAssistantMessages()
    {
        using var f = ReadyFixture(Sse(TextFrame("the answer"), Done(9, 2)));

        var result = await f.Build().RunAsync(Turn("what is up?"), new RecordingSink(), default);

        var conv = await f.Db.AIConversations.SingleAsync();
        Assert.Equal(result.ConversationId, conv.AIConversationId);
        Assert.Equal(User.ToString(), conv.UserId);
        var messages = conv.Messages.EnumerateArray().ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal("what is up?", messages[0].GetProperty("content").GetString());
        Assert.Equal("the answer", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TokenUsageIsPersisted()
    {
        using var f = ReadyFixture(Sse(TextFrame("x"), Done(9, 2)));

        await f.Build().RunAsync(Turn(), new RecordingSink(), default);

        var usage = (await f.Db.AIConversations.SingleAsync()).TokenUsage;
        Assert.Equal(9, usage.GetProperty("input").GetInt32());
        Assert.Equal(2, usage.GetProperty("output").GetInt32());
    }

    // Follow-up turns append to the same row and replay prior history to the
    // model — otherwise the agent has amnesia between messages.
    [Fact]
    public async Task FollowUpTurnAppendsAndReplaysHistory()
    {
        using var f = ReadyFixture(
            Sse(TextFrame("first answer"), Done()),
            Sse(TextFrame("second answer"), Done()));
        var runner = f.Build();
        var first = await runner.RunAsync(Turn("first question"), new RecordingSink(), default);

        await runner.RunAsync(
            Turn("second question", conversationId: first.ConversationId), new RecordingSink(), default);

        var conv = await f.Db.AIConversations.SingleAsync();
        Assert.Equal(4, conv.Messages.GetArrayLength());

        // The second request replays the earlier exchange before the new message.
        var contents = TestJson.Element(f.Llm.RequestBodies[1]).GetProperty("messages")
            .EnumerateArray().Select(m => m.GetProperty("content").GetString()).ToList();
        Assert.Contains("first question", contents);
        Assert.Contains("first answer", contents);
        Assert.Contains("second question", contents);
    }

    // A conversation born in the web chat is stamped `source = "web"`, so the
    // field never reads as null/missing data — the messaging worker stamps the
    // channel provider on its own rows.
    [Fact]
    public async Task NewWebConversationIsStampedWithTheWebSource()
    {
        using var f = ReadyFixture(Sse(TextFrame("x"), Done()));

        await f.Build().RunAsync(Turn(), new RecordingSink(), default);

        Assert.Equal("web", (await f.Db.AIConversations.SingleAsync()).Source);
    }

    // Ownership guard: a web conversation belonging to another user must not be
    // appended to.
    [Fact]
    public async Task WebConversationOwnedByAnotherUserIsNotAppended()
    {
        using var f = ReadyFixture(Sse(TextFrame("x"), Done()));
        var conversationId = Guid.NewGuid();
        f.Db.AIConversations.Add(new AIConversation
        {
            AIConversationId = conversationId,
            UserId = Guid.NewGuid().ToString(),   // somebody else
            Source = "web",
            Messages = TestJson.Element("[]"),
            Status = "active",
            IsActive = true,
        });
        await f.Db.SaveChangesAsync();

        await f.Build().RunAsync(Turn(conversationId: conversationId), new RecordingSink(), default);

        Assert.Empty((await f.Db.AIConversations.SingleAsync()).Messages.EnumerateArray());
    }

    // External (messaging) threads are owned by the channel, not a fixed user,
    // so the owner is refreshed rather than the write refused.
    [Fact]
    public async Task ExternalConversationRefreshesTheOwnerInsteadOfRefusing()
    {
        using var f = ReadyFixture(Sse(TextFrame("x"), Done()));
        var conversationId = Guid.NewGuid();
        f.Db.AIConversations.Add(new AIConversation
        {
            AIConversationId = conversationId,
            UserId = Guid.NewGuid().ToString(),
            Source = "telegram",
            Messages = TestJson.Element("[]"),
            Status = "active",
            IsActive = true,
        });
        await f.Db.SaveChangesAsync();

        await f.Build().RunAsync(Turn(conversationId: conversationId), new RecordingSink(), default);

        var conv = await f.Db.AIConversations.SingleAsync();
        Assert.Equal(User.ToString(), conv.UserId);
        Assert.Equal(2, conv.Messages.GetArrayLength());
    }

    // ─── agent run bookkeeping ──────────────────────────────────────────

    [Fact]
    public async Task AgentRunIsRecordedAsCompleted()
    {
        using var f = ReadyFixture(Sse(TextFrame("x"), Done(9, 2)));

        var result = await f.Build().RunAsync(Turn(), new RecordingSink(), default);

        var run = await f.Db.AgentRuns.SingleAsync();
        Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(result.ConversationId, run.ConversationId);
        Assert.Equal(9, run.TokensIn);
        Assert.Equal(2, run.TokensOut);
        Assert.NotNull(run.CompletedAt);
    }

    [Fact]
    public async Task AgentRunLogsTheToolCalls()
    {
        using var f = ReadyFixture(
            Sse(ToolFrame("call_1", "list_workflows", "{}"), Done()),
            Sse(TextFrame("ok"), Done()));
        f.RegisterTool("list_workflows");

        await f.Build().RunAsync(Turn(), new RecordingSink(), default);

        var run = await f.Db.AgentRuns.SingleAsync();
        var logged = Assert.Single(run.ToolCalls.EnumerateArray());
        Assert.Equal("list_workflows", logged.GetProperty("name").GetString());
    }

    // ─── failure containment ────────────────────────────────────────────

    // An upstream failure mid-turn comes back as a structured error AND the
    // partial turn is still persisted — losing the user's message would be
    // worse than the failure itself.
    [Fact]
    public async Task UpstreamFailureIsContainedAndTheTurnIsStillPersisted()
    {
        using var f = new Fixture();
        f.SeedProvider();
        f.SeedAgent();
        f.Llm = new ScriptedLlm();
        var failing = new FakeHttpMessageHandler(_ => throw new HttpRequestException("upstream down"));
        var caller = new FakeUser();
        var runner = new AgentConversationRunner(
            f.Db,
            new AIConversationRepository(f.Db),
            new AgentRunRepository(f.Db),
            new LlmProviderFactory(
                new AiProviderRepository(f.Db), new RepositoryBase<AiAgentModel>(f.Db), caller,
                new FakeHttpClientFactory(failing), new FakeCrypto(), NullLoggerFactory.Instance),
            f.Registry,
            new ToolDispatcher(
                f.Registry, new ServiceCollection().BuildServiceProvider(), caller, new FakeTrace(),
                new PermissionClassifier(NullLogger<PermissionClassifier>.Instance),
                new StubEffectivePermissions(), new FakeAppSettings(),
                NullLogger<ToolDispatcher>.Instance),
            new FakeSkillPromptLoader(), new ScopedSkillCatalog(f.Db, new FakeApiSpecIndex()),
            caller, new FakeTrace(), new ToolExecutionContext(),
            Options.Create(new AiChatOptions { StreamDeadlineSeconds = 30 }),
            NullLogger<AgentConversationRunner>.Instance);
        var sink = new RecordingSink();

        var result = await runner.RunAsync(Turn("my question"), sink, default);

        Assert.NotNull(result.Error);
        Assert.NotNull(sink.Error);
        var conv = await f.Db.AIConversations.SingleAsync();
        Assert.Equal("my question", conv.Messages[0].GetProperty("content").GetString());
        Assert.Contains("[error:", conv.Messages[1].GetProperty("content").GetString());
        Assert.Equal(AgentRunStatus.Failed, (await f.Db.AgentRuns.SingleAsync()).Status);
    }

    // ─── CapForLlm ──────────────────────────────────────────────────────

    // Oversized tool results are capped before they go back to the model — one
    // huge result would otherwise blow the context window and 400 the turn.
    [Fact]
    public void CapForLlm_LeavesShortResultsAlone()
    {
        Assert.Equal("short", AgentConversationRunner.CapForLlm("short", 100));
    }

    [Fact]
    public void CapForLlm_TruncatesAndExplainsWhy()
    {
        var raw = new string('x', 500);

        var capped = AgentConversationRunner.CapForLlm(raw, 100);

        Assert.StartsWith(new string('x', 100), capped);
        Assert.Contains("truncated 400 chars", capped);
        Assert.Contains("narrow the tool call", capped);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CapForLlm_NonPositiveLimitDisablesCapping(int max)
    {
        var raw = new string('x', 5000);

        Assert.Equal(raw, AgentConversationRunner.CapForLlm(raw, max));
    }

    [Fact]
    public void CapForLlm_ExactLengthIsNotTruncated()
    {
        var raw = new string('x', 100);

        Assert.Equal(raw, AgentConversationRunner.CapForLlm(raw, 100));
    }
}
