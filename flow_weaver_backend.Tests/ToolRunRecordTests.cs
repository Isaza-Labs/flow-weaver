using flow_weaver_backend.Services.Ai.Conversation;

namespace flow_weaver_backend.Tests;

// The history replayed to the model carries, for every reply, a server-written
// record of the tools that reply really ran: in front of the user message that
// followed it, never inside the reply, and absent for rows written before the
// record existed.
public class ToolRunRecordTests
{
    private static AgentConversationRunner.StoredMessage User(string text) => new("user", text);
    private static AgentConversationRunner.StoredMessage Reply(string text, List<StoredToolCall>? tools) =>
        new("assistant", text, tools);

    [Fact]
    public void A_reply_that_ran_nothing_says_so()
        => Assert.Contains("it ran no tools", ToolRunRecord.Render([]));

    [Fact]
    public void A_reply_lists_its_calls_and_counts_failures()
    {
        var record = ToolRunRecord.Render(
        [
            new("execute_operation", true, "{\"operation_id\":\"fw_inventory:list_devices\"}", "{\"status\":200}"),
            new("execute_operation", false, "{\"operation_id\":\"dcim_devices_list\"}", "{\"status_code\":403}"),
        ]);

        Assert.Contains("tools actually run: 2 (1 ok, 1 failed)", record);
        Assert.Contains("dcim_devices_list", record);
        Assert.Contains("FAILED", record);
    }

    [Fact]
    public void A_long_run_is_capped_and_the_rest_counted()
    {
        var calls = Enumerable.Range(0, 200)
            .Select(i => new StoredToolCall("discover_operations", true, $"{{\"i\":{i}}}", new string('x', 150)))
            .ToList();

        var record = ToolRunRecord.Render(calls);

        Assert.True(record.Length <= ToolRunRecord.MaxRenderChars + 200);
        Assert.Contains("more calls not listed", record);
    }

    [Fact]
    public void Secrets_in_arguments_and_results_are_not_kept()
    {
        var entry = ToolRunRecord.Entry(
            "create_credential",
            TestJson.Element("""{"name":"nb","password":"hunter2-secret","nested":{"api_key":"abc123"}}"""),
            true,
            """{"jwt":"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.c2lnbmF0dXJlc2lnbmF0dXJl"}""");

        Assert.DoesNotContain("hunter2", entry.Args);
        Assert.DoesNotContain("abc123", entry.Args);
        Assert.Contains(ToolRunRecord.RedactedMarker, entry.Args);
        Assert.Contains("\"name\":\"nb\"", entry.Args);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", entry.Result);
    }

    [Fact]
    public void Long_arguments_and_results_are_clipped()
    {
        var entry = ToolRunRecord.Entry("x", TestJson.Element($"{{\"q\":\"{new string('a', 500)}\"}}"), true, new string('r', 500));

        Assert.True(entry.Args.Length <= ToolRunRecord.MaxArgsChars + 1);
        Assert.True(entry.Result.Length <= ToolRunRecord.MaxResultChars + 1);
    }

    [Fact]
    public void Each_record_goes_in_front_of_the_message_after_its_reply()
    {
        var (kept, omitted, trailing) = AgentConversationRunner.ReplayHistory(
        [
            User("sincroniza netbox"), Reply("", []),
            User("continúa"), Reply("Hecho.", [new("execute_operation", true, "{}", "{\"id\":1}")]),
        ], charBudget: 0);

        Assert.Equal(0, omitted);
        Assert.Equal("sincroniza netbox", kept[0].Content);
        Assert.Equal("", kept[1].Content);
        Assert.StartsWith("[Flow Weaver server record of your previous reply: it ran no tools", kept[2].Content);
        Assert.EndsWith("continúa", kept[2].Content);
        Assert.Equal("Hecho.", kept[3].Content);
        Assert.NotNull(trailing);
        Assert.Contains("tools actually run: 1", trailing);
    }

    [Fact]
    public void Rows_written_before_the_record_existed_get_none()
    {
        var (kept, _, trailing) = AgentConversationRunner.ReplayHistory(
            [User("hola"), Reply("Hola.", null), User("sigue"), Reply("Ok.", null)], charBudget: 0);

        Assert.Equal("sigue", kept[2].Content);
        Assert.Null(trailing);
    }

    [Fact]
    public void A_kept_message_whose_reply_fell_out_of_the_window_loses_its_record()
    {
        var (kept, omitted, _) = AgentConversationRunner.ReplayHistory(
        [
            User(new string('u', 500)), Reply(new string('a', 500), []),
            User("continúa"), Reply("Ok.", []),
        ], charBudget: 300);

        Assert.Equal(2, omitted);
        Assert.Equal("continúa", kept[0].Content);
    }

    [Fact]
    public void Stored_tools_are_read_back_and_absent_ones_are_null()
    {
        var withTools = TestJson.Element(
            """{"role":"assistant","content":"x","tools":[{"name":"a","ok":false,"args":"{}","result":"r"}]}""");
        var legacy = TestJson.Element("""{"role":"assistant","content":"x"}""");

        var read = Assert.Single(ToolRunRecord.Read(withTools)!);
        Assert.Equal("a", read.Name);
        Assert.False(read.Ok);
        Assert.Null(ToolRunRecord.Read(legacy));
    }
}
