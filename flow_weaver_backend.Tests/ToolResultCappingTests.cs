using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Ai.Conversation;
using flow_weaver_backend.Services.Ai.Tools.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Guards the fix for the chat 400 (context overflow): a single oversized
// tool result must not reach the model whole. Two layers — the controller
// cap (CapForLlm) and the get_step_logs payload cap.
public class ToolResultCappingTests
{
    private static readonly FakeUser Caller = new();

    [Fact]
    public void CapForLlm_truncates_oversized_with_marker()
    {
        var raw = new string('x', 10_000);
        var capped = AgentConversationRunner.CapForLlm(raw, 1_000);
        Assert.True(capped.Length < raw.Length);
        Assert.Contains("truncated", capped);
    }

    [Fact]
    public void CapForLlm_leaves_small_untouched()
    {
        const string raw = "small result";
        Assert.Equal(raw, AgentConversationRunner.CapForLlm(raw, 1_000));
    }

    [Fact]
    public async Task GetStepLogs_caps_large_output_payload()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(nameof(GetStepLogs_caps_large_output_payload)).Options;
        using var db = new AppDbContext(options);

        var stepId = Guid.NewGuid();
        var bigPayload = JsonSerializer.SerializeToElement(new { dump = new string('x', 20_000) });
        db.StepRuns.Add(new StepRun
        {
            StepRunId = stepId,
            WorkflowRunId = Guid.NewGuid(),
            NodeId = "ssh-collect",
            Status = "completed",
            Logs = "ok",
            OutputPayload = bigPayload,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();

        var handler = new GetStepLogsHandler(new StepRunRepository(db), Caller, NullLogger<GetStepLogsHandler>.Instance);
        var args = JsonSerializer.SerializeToElement(new { step_run_id = stepId.ToString() });
        var result = await handler.ExecuteAsync(args, CancellationToken.None);

        var outPayload = result.GetProperty("output_payload");
        Assert.Equal(JsonValueKind.String, outPayload.ValueKind);
        Assert.Contains("too large", outPayload.GetString());
    }
}
