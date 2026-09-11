namespace flow_weaver_backend.Services.Ai.Providers;

// The provider types a deployment can configure, in one place: the list, each
// type's default endpoint, and which of them requires a base URL.
//
// Before this, nothing validated the type at all — AIProviderService only checked
// that the field was non-empty, so "claude", "gpt" or a typo saved happily and
// failed later inside a turn, as "unsupported provider type" from the factory,
// with the conversation already open.
//
// Most entries are OpenAI-compatible endpoints that differ only by URL; Anthropic,
// Gemini and Ollama have their own wire formats and their own provider classes.
// `custom` is the escape hatch — any other OpenAI-compatible endpoint — which is
// why it is the one type whose base URL is required.
public static class LlmProviderCatalog
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";
    public const string DeepSeek = "deepseek";
    public const string Kimi = "kimi";
    public const string Ollama = "ollama";
    public const string Custom = "custom";

    public static readonly string[] Types =
        [OpenAi, Anthropic, Gemini, DeepSeek, Kimi, Ollama, Custom];

    public static bool IsSupported(string? type)
        => type is not null && Types.Contains(type.Trim().ToLowerInvariant());

    // `custom` points at an endpoint only the operator knows. Everything else has a
    // documented default, so a blank base URL is the normal case there.
    public static bool RequiresBaseUrl(string? type)
        => string.Equals(type?.Trim(), Custom, StringComparison.OrdinalIgnoreCase);

    // The endpoint used when a provider row leaves BaseURL blank. Null for custom:
    // there is nothing to fall back to, which is what RequiresBaseUrl enforces at
    // the edge.
    public static string? DefaultBaseUrl(string type) => type.Trim().ToLowerInvariant() switch
    {
        OpenAi => "https://api.openai.com",
        Anthropic => "https://api.anthropic.com",
        Gemini => "https://generativelanguage.googleapis.com",
        DeepSeek => "https://api.deepseek.com",
        Kimi => "https://api.moonshot.ai",
        Ollama => "http://localhost:11434",
        _ => null,
    };
}
