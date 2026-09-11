using System.Text.Json.Serialization;
using flow_weaver_backend.Services.Ai.Conversation;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

// Chat with tool-calling loop + SSE streaming. The agent executes tools
// autonomously (within permission bounds) until it produces a final text
// response or hits MaxIterations.
//
// The loop itself lives in IAgentConversationRunner so the same code path
// serves the web chat (SSE sink), the non-streaming endpoint (null sink), and
// the messaging worker (null sink, F2). This controller only adapts HTTP <->
// the runner.
[ApiController]
[Route("api/ai/chat")]
[HasPermission("ai.chat")]
public class AiChatController : ControllerBase
{
    private readonly IAgentConversationRunner _runner;

    public AiChatController(IAgentConversationRunner runner)
    {
        _runner = runner;
    }

    // Non-streaming: full tool-calling loop, returns final response.
    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.AiChat)]
    public async Task<IActionResult> Chat([FromBody] V2ChatRequest request, CancellationToken ct)
    {
        var result = await _runner.RunAsync(
            new AgentTurnRequest
            {
                Message = request.Message,
                AgentId = request.AgentId,
                ConversationId = request.ConversationId,
            },
            NullAgentEventSink.Instance,
            ct);

        if (result.Error is not null && result.FinalText.Length == 0 && result.ToolCalls.Count == 0)
            return StatusCode(500, new { error = result.Error });

        return Ok(new
        {
            content = result.FinalText,
            conversation_id = result.ConversationId,
            tool_calls = result.ToolCalls.Select(t => new
            {
                tool = t.Name,
                arguments = t.Arguments,
                success = t.Success,
            }),
            tokens = new { input = result.TokensIn, output = result.TokensOut },
        });
    }

    // SSE streaming: sends text deltas + tool events with a hard wall-clock
    // deadline so a stuck tool or runaway loop can't bust the chat SLO. Event
    // shapes are defined by SseAgentEventSink (the frontend's wire contract):
    //   conversation → {type, id, is_new}
    //   text         → {type, content}
    //   tool_start   → {type, name, args_preview}
    //   tool_result  → {type, name, success, preview, attachment?}
    //   timeout      → {type, partial}
    //   done         → {type, tokens_in, tokens_out, iterations}
    //   error        → {type, message, code?}
    [HttpPost("stream")]
    [EnableRateLimiting(RateLimitingConfiguration.AiChat)]
    public async Task Stream([FromBody] V2ChatRequest request, CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var sink = new SseAgentEventSink(Response);
        await _runner.RunAsync(
            new AgentTurnRequest
            {
                Message = request.Message,
                AgentId = request.AgentId,
                ConversationId = request.ConversationId,
            },
            sink,
            ct);
    }

    // GET /api/ai/permissions/matrix — admin-only, shows the full permission table.
    //
    // `implemented` matters: the matrix deliberately carries forward-looking rows for
    // tools no handler answers to yet (see PermissionClassifier). Without the flag an
    // administrator reads 77 rows with tiers beside them and cannot tell which 29 are
    // intentions — and granting one of those changes nothing while looking identical to
    // granting one that works. Computed from the registry rather than listed, because
    // the hand-kept list of which ones they are had already drifted.
    [HttpGet("/api/ai/permissions/matrix")]
    [HasPermission("ai.permissions.read")]
    public IActionResult PermissionsMatrix()
    {
        var implemented = Services.Ai.Tools.AgentToolHandlers.RegisteredToolNames;
        return Ok(Services.Ai.Permissions.PermissionClassifier.Matrix
            .Select(kv => new
            {
                tool = kv.Key,
                domain = kv.Value.Domain,
                level = kv.Value.Level,
                implemented = implemented.Contains(kv.Key),
            }));
    }
}

// Client sends snake_case — match it explicitly. ASP.NET Core's default JSON
// binder is case-insensitive but still structural, so `agent_id` would silently
// drop onto an `AgentId` field without the attribute. That's the bug that made
// every chat message spawn a new conversation: the frontend's `conversation_id`
// was being ignored at bind time.
public class V2ChatRequest
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("agent_id")]
    public Guid? AgentId { get; set; }

    [JsonPropertyName("conversation_id")]
    public Guid? ConversationId { get; set; }
}
