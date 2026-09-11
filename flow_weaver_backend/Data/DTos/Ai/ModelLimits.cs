using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Prompt and output token budget for a provider call.
// Parsed from AIProvider.Config.model_limits so limits can be tuned per provider
// without schema changes. Normalize() clamps absurd or contradictory values.
//
// Consumed by the providers: Anthropic and Gemini send MaxOutputTokens instead of
// their built-in default (the API requires a number), OpenAI sends it only when
// configured (its default is the model's own maximum), Ollama sends both numbers
// as num_ctx / num_predict — without num_ctx Ollama runs the model at its default
// window and silently drops the top of a prompt that does not fit, which is where
// the system prompt lives. Null (no `model_limits` in Config) means "leave the
// provider's own default alone", so a provider row that predates this is unchanged.
public class ModelLimits
{
    public const string ConfigKey = "model_limits";

    // The `model_limits` object of a provider's Config, normalised; null when the
    // config has none or it is not an object. A `max_output_tokens` given without
    // `context_window` gets a window large enough not to clamp it — the window is
    // the less commonly known number, and forcing someone to look it up to raise
    // the output cap would be the wrong trade.
    public static ModelLimits? FromConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object) return null;
        if (!config.TryGetProperty(ConfigKey, out var node) || node.ValueKind != JsonValueKind.Object) return null;

        ModelLimits? limits;
        try { limits = node.Deserialize<ModelLimits>(); }
        catch (JsonException) { return null; }
        if (limits is null) return null;

        var hasWindow = node.TryGetProperty("context_window", out var cw)
            && cw.ValueKind == JsonValueKind.Number && cw.TryGetInt32(out var cwv) && cwv >= MinContextWindowTokens;
        if (!hasWindow)
            limits.ContextWindowTokens = Math.Max(DefaultContextWindowTokens, limits.MaxOutputTokens * 2);

        return limits.Normalize();
    }

    public const int DefaultContextWindowTokens = 8192;
    public const int DefaultMaxOutputTokens = 2048;
    private const int MinContextWindowTokens = 2048;
    private const int MinMaxOutputTokens = 256;

    [JsonPropertyName("context_window")]
    public int ContextWindowTokens { get; set; } = DefaultContextWindowTokens;

    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; set; } = DefaultMaxOutputTokens;

    // Clamps values to sane bounds. Guarantees MaxOutputTokens < ContextWindowTokens.
    public ModelLimits Normalize()
    {
        if (ContextWindowTokens < MinContextWindowTokens)
            ContextWindowTokens = DefaultContextWindowTokens;
        if (MaxOutputTokens < MinMaxOutputTokens)
            MaxOutputTokens = DefaultMaxOutputTokens;
        if (MaxOutputTokens >= ContextWindowTokens)
        {
            MaxOutputTokens = ContextWindowTokens / 4;
            if (MaxOutputTokens < MinMaxOutputTokens)
                MaxOutputTokens = MinMaxOutputTokens;
        }
        return this;
    }

    // Tokens available for prompt content after reserving MaxOutputTokens.
    public int InputTokenBudget()
    {
        Normalize();
        var input = ContextWindowTokens - MaxOutputTokens;
        if (input < MinContextWindowTokens / 2)
            input = ContextWindowTokens / 2;
        if (input < 512)
            input = 512;
        return input;
    }

    // Conservative character estimate (~3 chars/token) used to truncate live context
    // before it is combined with the system prompt.
    public int InputCharBudget() => InputTokenBudget() * 3;
}
