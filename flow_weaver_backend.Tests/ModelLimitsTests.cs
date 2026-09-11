using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Tests;

// ModelLimits is read from AIProvider.Config.model_limits and decides the output
// cap every provider sends (and Ollama's context window). It sat unused for a
// while; these pin the parsing now that the providers depend on it.
public class ModelLimitsTests
{
    [Fact]
    public void No_model_limits_in_config_means_null()
    {
        Assert.Null(ModelLimits.FromConfig(TestJson.Element("""{}""")));
        Assert.Null(ModelLimits.FromConfig(TestJson.Element("""{"other":1}""")));
        Assert.Null(ModelLimits.FromConfig(TestJson.Element("""{"model_limits":"nope"}""")));
        Assert.Null(ModelLimits.FromConfig(default));
    }

    [Fact]
    public void Both_numbers_are_read_as_given()
    {
        var limits = ModelLimits.FromConfig(TestJson.Element("""{"model_limits":{"context_window":200000,"max_output_tokens":16000}}"""));

        Assert.NotNull(limits);
        Assert.Equal(200_000, limits!.ContextWindowTokens);
        Assert.Equal(16_000, limits.MaxOutputTokens);
    }

    // The window is the less commonly known number; raising the output cap must
    // not require looking it up, and must not be clamped by a default window.
    [Fact]
    public void An_output_cap_without_a_window_is_not_clamped()
    {
        var limits = ModelLimits.FromConfig(TestJson.Element("""{"model_limits":{"max_output_tokens":16000}}"""));

        Assert.NotNull(limits);
        Assert.Equal(16_000, limits!.MaxOutputTokens);
        Assert.True(limits.ContextWindowTokens > limits.MaxOutputTokens);
    }

    [Fact]
    public void A_window_without_an_output_cap_uses_the_default_cap()
    {
        var limits = ModelLimits.FromConfig(TestJson.Element("""{"model_limits":{"context_window":32768}}"""));

        Assert.NotNull(limits);
        Assert.Equal(32_768, limits!.ContextWindowTokens);
        Assert.Equal(ModelLimits.DefaultMaxOutputTokens, limits.MaxOutputTokens);
    }

    // Contradictory values are clamped rather than sent: an output cap at or above
    // the window would be rejected by every vendor.
    [Fact]
    public void An_output_cap_at_or_above_the_window_is_clamped()
    {
        var limits = ModelLimits.FromConfig(TestJson.Element("""{"model_limits":{"context_window":8192,"max_output_tokens":8192}}"""));

        Assert.NotNull(limits);
        Assert.True(limits!.MaxOutputTokens < limits.ContextWindowTokens);
    }

    [Fact]
    public void Unparseable_limits_are_treated_as_absent()
    {
        Assert.Null(ModelLimits.FromConfig(TestJson.Element("""{"model_limits":{"context_window":"lots"}}""")));
    }
}
