using flow_weaver_backend.Services.Ai;
using flow_weaver_backend.Services.Ai.Conversation;
using flow_weaver_backend.Services.Ai.Providers;

namespace flow_weaver_backend.Tests;

// The conversation history sent to the model is a window, not the whole thread.
// Every turn used to replay every message, so a long conversation grew until the
// provider refused it (400) or a gateway trimmed it from the top — where the
// system prompt is. The window is the newest messages that fit a character
// budget, cut on turn boundaries; what falls out is counted so the runner can say
// so to the model instead of letting it assume it remembers.
public class HistoryWindowTests
{
    private static LlmMessage Msg(string role, int chars) =>
        new() { Role = role, Content = new string(role == "user" ? 'u' : 'a', chars) };

    [Fact]
    public void A_budget_of_zero_means_no_window()
    {
        var history = new List<LlmMessage> { Msg("user", 100), Msg("assistant", 100), Msg("user", 100), Msg("assistant", 100) };

        var (kept, omitted) = AgentConversationRunner.WindowHistory(history, 0);

        Assert.Equal(4, kept.Count);
        Assert.Equal(0, omitted);
    }

    [Fact]
    public void History_that_fits_is_sent_whole()
    {
        var history = new List<LlmMessage> { Msg("user", 100), Msg("assistant", 100), Msg("user", 100), Msg("assistant", 100) };

        var (kept, omitted) = AgentConversationRunner.WindowHistory(history, 1_000);

        Assert.Equal(4, kept.Count);
        Assert.Equal(0, omitted);
    }

    [Fact]
    public void The_newest_messages_survive_and_the_oldest_are_dropped()
    {
        var history = new List<LlmMessage>
        {
            Msg("user", 100), Msg("assistant", 900),   // turn 1: 1000
            Msg("user", 100), Msg("assistant", 900),   // turn 2: 1000
            Msg("user", 50),  Msg("assistant", 50),    // turn 3: 100
        };

        // 1_200 holds turns 3 and 2 (1_100) but not turn 1 on top of them.
        var (kept, omitted) = AgentConversationRunner.WindowHistory(history, 1_200);

        Assert.Equal(4, kept.Count);
        Assert.Equal(2, omitted);
        Assert.Same(history[2], kept[0]);
        Assert.Same(history[5], kept[3]);
    }

    // The window never opens on the agent's half of a turn: an assistant message
    // without the question it answers reads as the model talking to itself.
    [Fact]
    public void The_window_never_starts_on_an_assistant_message()
    {
        var history = new List<LlmMessage>
        {
            Msg("user", 500), Msg("assistant", 100),
            Msg("user", 100), Msg("assistant", 100),
        };

        // 350 fits turn 2 (200) and the assistant half of turn 1 (100) but not its
        // user half (500) — so turn 1 goes entirely.
        var (kept, omitted) = AgentConversationRunner.WindowHistory(history, 350);

        Assert.Equal(2, kept.Count);
        Assert.Equal("user", kept[0].Role);
        Assert.Equal(2, omitted);
    }

    // A budget smaller than the last exchange still sends that exchange: the model
    // has to see at least what was just said, whatever the number in appsettings.
    [Fact]
    public void The_most_recent_turn_is_always_sent_even_over_budget()
    {
        var history = new List<LlmMessage>
        {
            Msg("user", 100), Msg("assistant", 100),
            Msg("user", 5_000), Msg("assistant", 5_000),
        };

        var (kept, omitted) = AgentConversationRunner.WindowHistory(history, 1_000);

        Assert.Equal(2, kept.Count);
        Assert.Equal(2, omitted);
    }

    [Fact]
    public void Empty_history_stays_empty()
    {
        var (kept, omitted) = AgentConversationRunner.WindowHistory([], 1_000);

        Assert.Empty(kept);
        Assert.Equal(0, omitted);
    }

    [Fact]
    public void The_omission_note_names_the_count_and_tells_the_model_to_ask()
    {
        var note = AgentConversationRunner.HistoryOmittedNote(6);

        Assert.Contains("6", note);
        Assert.Contains("ask", note, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("length", true)]
    [InlineData("LENGTH", true)]
    [InlineData("max_tokens", true)]
    [InlineData("MAX_TOKENS", true)]
    [InlineData("stop", false)]
    [InlineData("end_turn", false)]
    [InlineData("tool_calls", false)]
    [InlineData(null, false)]
    public void Only_out_of_tokens_stop_reasons_count_as_cut_off(string? reason, bool expected)
    {
        Assert.Equal(expected, AgentConversationRunner.OutputCutOff(reason));
    }

    // The defaults the turn budget ships with, pinned so they cannot drift apart
    // from the transport backstop or quietly shrink.
    [Fact]
    public void Chat_option_defaults_are_the_documented_ones()
    {
        var options = new AiChatOptions();

        Assert.Equal(240, options.StreamDeadlineSeconds);
        Assert.Equal(100_000, options.MaxToolResultChars);
        Assert.Equal(60_000, options.HistoryCharBudget);
        Assert.True(options.StreamDeadlineSeconds <= 300 - 60,
            "the deadline must stay at least a minute under the 5-minute llm HttpClient timeout in Program.cs");
    }
}
