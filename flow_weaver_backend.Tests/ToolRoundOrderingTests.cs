using System.Text.Json;
using flow_weaver_backend.Services.Ai.Conversation;
using flow_weaver_backend.Services.Ai.Providers;

namespace flow_weaver_backend.Tests;

// A round of parallel tool calls in which one call auto-loads a skill. The skill
// text used to be appended right behind the result of the call that loaded it, so
// with two calls the history read: result 1, skill text, result 2. Anthropic maps
// all three into one user turn and requires its tool_result blocks to come first;
// text before the second result was answered with a 400 ("tool_use ids were found
// without tool_result blocks immediately after"), and the turn failed.
public class ToolRoundOrderingTests
{
    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement;

    private static LlmMessage Result(string id) => new() { Role = "tool", Content = $"result of {id}", ToolCallId = id };
    private static LlmMessage Skill(string text) => new() { Role = "system", Content = text };

    private static List<LlmMessage> HistoryWithParallelRound()
        => [
            new LlmMessage { Role = "system", Content = "prompt" },
            new LlmMessage { Role = "user", Content = "check both devices" },
            new LlmMessage
            {
                Role = "assistant",
                Content = "",
                ToolCalls =
                [
                    new ToolCallResult { Id = "call_1", Name = "netbox_get_device", Arguments = NoArgs },
                    new ToolCallResult { Id = "call_2", Name = "netbox_get_device", Arguments = NoArgs },
                ],
            },
        ];

    private static List<Dictionary<string, object?>> Blocks(Dictionary<string, object?> turn)
        => (List<Dictionary<string, object?>>)turn["content"]!;

    [Fact]
    public void Results_of_a_parallel_round_lead_the_user_turn_and_the_skill_text_follows()
    {
        var messages = HistoryWithParallelRound();
        // The skill was loaded by the first call, i.e. before the second result existed.
        AgentConversationRunner.AppendToolRound(
            messages, [Result("call_1"), Result("call_2")], [Skill("NetBox skill rules")]);

        var (_, mapped) = AnthropicProvider.MapMessages(messages);
        var turn = mapped[^1];
        Assert.Equal("user", turn["role"]);

        var types = Blocks(turn).Select(b => (string)b["type"]!).ToList();
        Assert.Equal(["tool_result", "tool_result", "text"], types);
        Assert.Equal(["call_1", "call_2"], Blocks(turn).Take(2).Select(b => (string)b["tool_use_id"]!));
        Assert.Equal("NetBox skill rules", Blocks(turn)[2]["text"]);
    }

    [Fact]
    public void Every_tool_use_of_the_round_is_answered_in_the_turn_right_after_it()
    {
        var messages = HistoryWithParallelRound();
        AgentConversationRunner.AppendToolRound(
            messages, [Result("call_1"), Result("call_2")], [Skill("integration skill"), Skill("topic skill")]);

        var (_, mapped) = AnthropicProvider.MapMessages(messages);
        var assistant = mapped[^2];
        Assert.Equal("assistant", assistant["role"]);
        var asked = Blocks(assistant).Where(b => (string)b["type"]! == "tool_use").Select(b => (string)b["id"]!).ToList();

        // Anthropic's rule, stated directly: the next turn opens with one tool_result
        // per tool_use, before any text.
        var leading = Blocks(mapped[^1])
            .TakeWhile(b => (string)b["type"]! == "tool_result")
            .Select(b => (string)b["tool_use_id"]!)
            .ToList();
        Assert.Equal(asked, leading);
    }

    [Fact]
    public void A_round_that_loads_no_skill_is_only_its_results()
    {
        var messages = HistoryWithParallelRound();
        var before = messages.Count;
        AgentConversationRunner.AppendToolRound(messages, [Result("call_1"), Result("call_2")], []);

        Assert.Equal(before + 2, messages.Count);
        Assert.All(messages.Skip(before), m => Assert.Equal("tool", m.Role));
    }
}
