using System.Text;
using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Services.Ai.Conversation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Tests;

// The two chat entry points. The controller is thin — the runner does the
// work — but it owns one decision that matters: WHEN a turn counts as a
// failure. A turn that produced text or ran tools before hitting an error
// still has something useful for the user, so it comes back as a normal 200;
// only a turn with nothing at all becomes a 500.
public class AiChatControllerTests
{
    private sealed class ScriptedRunner : IAgentConversationRunner
    {
        public AgentTurnResult Result { get; set; } = new()
        {
            ConversationId = Guid.NewGuid(),
            IsNewConversation = true,
        };
        public AgentTurnRequest? LastRequest { get; private set; }
        public IAgentEventSink? LastSink { get; private set; }

        public Task<AgentTurnResult> RunAsync(
            AgentTurnRequest request, IAgentEventSink sink, CancellationToken ct)
        {
            LastRequest = request;
            LastSink = sink;
            return Task.FromResult(Result);
        }
    }

    private static (AiChatController Controller, ScriptedRunner Runner, DefaultHttpContext Http) Build()
    {
        var runner = new ScriptedRunner();
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        var controller = new AiChatController(runner)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return (controller, runner, http);
    }

    private static AgentToolCall ToolCall(string name = "list_workflows", bool success = true)
        => new(name, TestJson.Element("""{"limit":5}"""), success);

    private static JsonElement BodyOf(IActionResult result)
        => JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<ObjectResult>(result).Value!);

    // ─── Chat (non-streaming) ───────────────────────────────────────────

    [Fact]
    public async Task Chat_ForwardsTheRequestToTheRunner()
    {
        var (controller, runner, _) = Build();
        var agentId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        await controller.Chat(new V2ChatRequest
        {
            Message = "list my workflows",
            AgentId = agentId,
            ConversationId = conversationId,
        }, default);

        Assert.Equal("list my workflows", runner.LastRequest!.Message);
        Assert.Equal(agentId, runner.LastRequest.AgentId);
        Assert.Equal(conversationId, runner.LastRequest.ConversationId);
    }

    // The non-streaming path has nowhere to push events, so it hands the
    // runner a sink that discards them.
    [Fact]
    public async Task Chat_UsesTheNullSink()
    {
        var (controller, runner, _) = Build();

        await controller.Chat(new V2ChatRequest { Message = "hi" }, default);

        Assert.Same(NullAgentEventSink.Instance, runner.LastSink);
    }

    [Fact]
    public async Task Chat_ReturnsTheFinalTextAndConversationId()
    {
        var (controller, runner, _) = Build();
        var conversationId = Guid.NewGuid();
        runner.Result = new AgentTurnResult
        {
            ConversationId = conversationId,
            IsNewConversation = true,
            FinalText = "you have 3 workflows",
            TokensIn = 120,
            TokensOut = 45,
        };

        var body = BodyOf(await controller.Chat(new V2ChatRequest { Message = "hi" }, default));

        Assert.Equal("you have 3 workflows", body.GetProperty("content").GetString());
        Assert.Equal(conversationId, body.GetProperty("conversation_id").GetGuid());
        Assert.Equal(120, body.GetProperty("tokens").GetProperty("input").GetInt32());
        Assert.Equal(45, body.GetProperty("tokens").GetProperty("output").GetInt32());
    }

    // The tool calls are surfaced so the UI can render what the agent did,
    // including the ones that failed.
    [Fact]
    public async Task Chat_SurfacesEveryToolCallWithItsOutcome()
    {
        var (controller, runner, _) = Build();
        runner.Result = new AgentTurnResult
        {
            ConversationId = Guid.NewGuid(),
            IsNewConversation = false,
            FinalText = "done",
            ToolCalls = new[] { ToolCall("list_workflows"), ToolCall("run_workflow", success: false) },
        };

        var body = BodyOf(await controller.Chat(new V2ChatRequest { Message = "hi" }, default));

        var calls = body.GetProperty("tool_calls").EnumerateArray().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("list_workflows", calls[0].GetProperty("tool").GetString());
        Assert.True(calls[0].GetProperty("success").GetBoolean());
        Assert.False(calls[1].GetProperty("success").GetBoolean());
        Assert.Equal(5, calls[0].GetProperty("arguments").GetProperty("limit").GetInt32());
    }

    // A turn that failed with nothing to show is a real 500 — there is no
    // partial answer to hand back.
    [Fact]
    public async Task Chat_AnEmptyFailedTurnIs500()
    {
        var (controller, runner, _) = Build();
        runner.Result = new AgentTurnResult
        {
            ConversationId = Guid.NewGuid(),
            IsNewConversation = true,
            Error = "provider unreachable",
        };

        var result = await controller.Chat(new V2ChatRequest { Message = "hi" }, default);

        Assert.Equal(500, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal("provider unreachable", BodyOf(result).GetProperty("error").GetString());
    }

    // But a turn that produced text before erroring still has value, so it
    // comes back as a normal answer rather than a blank 500.
    [Fact]
    public async Task Chat_AFailedTurnWithPartialTextIsStillAnAnswer()
    {
        var (controller, runner, _) = Build();
        runner.Result = new AgentTurnResult
        {
            ConversationId = Guid.NewGuid(),
            IsNewConversation = true,
            FinalText = "I found 3 workflows but then",
            Error = "provider dropped the connection",
        };

        var result = await controller.Chat(new V2ChatRequest { Message = "hi" }, default);

        Assert.Equal(200, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal("I found 3 workflows but then", BodyOf(result).GetProperty("content").GetString());
    }

    // Same for a turn that ran tools: the side effects already happened, so
    // reporting a bare 500 would hide them.
    [Fact]
    public async Task Chat_AFailedTurnThatRanToolsIsStillAnAnswer()
    {
        var (controller, runner, _) = Build();
        runner.Result = new AgentTurnResult
        {
            ConversationId = Guid.NewGuid(),
            IsNewConversation = true,
            ToolCalls = new[] { ToolCall("run_workflow") },
            Error = "timed out waiting for the model",
        };

        var result = await controller.Chat(new V2ChatRequest { Message = "hi" }, default);

        Assert.Equal(200, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Single(BodyOf(result).GetProperty("tool_calls").EnumerateArray());
    }

    [Fact]
    public async Task Chat_ASuccessfulTurnWithNoToolsReportsAnEmptyList()
    {
        var (controller, runner, _) = Build();
        runner.Result = new AgentTurnResult
        {
            ConversationId = Guid.NewGuid(),
            IsNewConversation = true,
            FinalText = "hello",
        };

        var body = BodyOf(await controller.Chat(new V2ChatRequest { Message = "hi" }, default));

        Assert.Empty(body.GetProperty("tool_calls").EnumerateArray());
    }

    // ─── Stream ─────────────────────────────────────────────────────────

    // The SSE headers have to be set BEFORE the runner writes its first
    // frame, or the browser buffers the whole response and the chat appears
    // frozen until the turn ends.
    [Fact]
    public async Task Stream_SetsTheEventStreamHeaders()
    {
        var (controller, _, http) = Build();

        await controller.Stream(new V2ChatRequest { Message = "hi" }, default);

        Assert.Equal("text/event-stream", http.Response.ContentType);
        Assert.Equal("no-cache", http.Response.Headers["Cache-Control"]);
        // Disables nginx's proxy buffering, which otherwise holds the frames.
        Assert.Equal("no", http.Response.Headers["X-Accel-Buffering"]);
    }

    [Fact]
    public async Task Stream_ForwardsTheRequestToTheRunner()
    {
        var (controller, runner, _) = Build();
        var conversationId = Guid.NewGuid();

        await controller.Stream(new V2ChatRequest
        {
            Message = "stream this",
            ConversationId = conversationId,
        }, default);

        Assert.Equal("stream this", runner.LastRequest!.Message);
        Assert.Equal(conversationId, runner.LastRequest.ConversationId);
    }

    // The streaming path hands the runner a live SSE sink bound to the
    // response — that is the whole difference from the non-streaming one.
    [Fact]
    public async Task Stream_HandsTheRunnerALiveSseSink()
    {
        var (controller, runner, _) = Build();

        await controller.Stream(new V2ChatRequest { Message = "hi" }, default);

        Assert.IsType<SseAgentEventSink>(runner.LastSink);
    }

    // Whatever the sink wrote reaches the wire in SSE framing.
    [Fact]
    public async Task Stream_TheSinksFramesReachTheResponseBody()
    {
        var (controller, runner, http) = Build();
        var conversationId = Guid.NewGuid();
        runner.Result = new AgentTurnResult { ConversationId = conversationId, IsNewConversation = true };

        await controller.Stream(new V2ChatRequest { Message = "hi" }, default);
        await runner.LastSink!.TextAsync("hola", default);

        var body = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());
        Assert.Contains("data: ", body);
        Assert.Contains("\"content\":\"hola\"", body);
    }
}
