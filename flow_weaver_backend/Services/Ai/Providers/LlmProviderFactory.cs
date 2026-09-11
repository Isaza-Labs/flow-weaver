using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Identity;
using AiAgentModel = flow_weaver_backend.Models.AIAgent;

namespace flow_weaver_backend.Services.Ai.Providers;

// Resolves an IStreamingToolCallingLlmProvider from an AIProvider row.
// Decrypts the API key, picks the right implementation by Type, and
// injects the correct base URL.
public class LlmProviderFactory
{
    private readonly IAiProviderRepository _providers;
    private readonly IRepository<AiAgentModel> _agents;
    private readonly ICurrentUser _caller;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ICredentialEncryptionService _crypto;
    private readonly ILoggerFactory _loggerFactory;

    public LlmProviderFactory(
        IAiProviderRepository providers,
        IRepository<AiAgentModel> agents,
        ICurrentUser caller,
        IHttpClientFactory httpFactory,
        ICredentialEncryptionService crypto,
        ILoggerFactory loggerFactory)
    {
        _providers = providers;
        _agents = agents;
        _caller = caller;
        _httpFactory = httpFactory;
        _crypto = crypto;
        _loggerFactory = loggerFactory;
    }

    public async Task<IStreamingToolCallingLlmProvider> ResolveAsync(
        Guid providerId, CancellationToken ct)
    {
        var provider = await _providers.GetByIdAsync(providerId, tracking: false, ct: ct)
            ?? throw new InvalidOperationException($"AI provider {providerId} not found");

        if (!provider.Enabled)
            throw new InvalidOperationException($"AI provider '{provider.Name}' is disabled");

        var apiKey = _crypto.Decrypt(provider.EncryptedApiKey) ?? string.Empty;
        var http = _httpFactory.CreateClient("llm");
        // Config.model_limits, when the row has one; null keeps each provider's own
        // default. Read here, once per resolve, so the providers stay config-free.
        var limits = ModelLimits.FromConfig(provider.Config);

        var type = provider.Type.Trim().ToLowerInvariant();

        // A blank base URL means "the vendor's own endpoint"; a filled one is a
        // proxy, a self-hosted deployment, or — for `custom` — the only address
        // there is.
        var baseUrl = string.IsNullOrWhiteSpace(provider.BaseURL)
            ? LlmProviderCatalog.DefaultBaseUrl(type)
            : provider.BaseURL;

        if (LlmProviderCatalog.RequiresBaseUrl(type) && string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                $"AI provider '{provider.Name}' is a custom endpoint and has no base URL configured");

        return type switch
        {
            LlmProviderCatalog.Anthropic => new AnthropicProvider(
                http, apiKey, baseUrl, _loggerFactory.CreateLogger<AnthropicProvider>(), limits),
            LlmProviderCatalog.Gemini => new GeminiProvider(
                http, apiKey, baseUrl, _loggerFactory.CreateLogger<GeminiProvider>(), limits),
            LlmProviderCatalog.Ollama => new OllamaProvider(
                http, baseUrl, _loggerFactory.CreateLogger<OllamaProvider>(), limits, apiKey),

            // DeepSeek, Kimi and whatever runs behind `custom` are OpenAI-compatible:
            // same body, same SSE, different host. The type is passed through so the
            // logs name the provider that was actually called.
            LlmProviderCatalog.OpenAi or LlmProviderCatalog.DeepSeek
                or LlmProviderCatalog.Kimi or LlmProviderCatalog.Custom => new OpenAiProvider(
                    http, apiKey, baseUrl, _loggerFactory.CreateLogger<OpenAiProvider>(), limits,
                    providerType: type),

            _ => throw new InvalidOperationException($"unsupported provider type: {provider.Type}"),
        };
    }

    public async Task<(IStreamingToolCallingLlmProvider provider, string model)> ResolveForAgentAsync(
        Guid agentId, CancellationToken ct)
    {
        var agent = await _agents.GetByIdAsync(agentId, tracking: false, ct: ct)
            ?? throw new InvalidOperationException($"AI agent {agentId} not found");

        if (!agent.ProviderId.HasValue)
            throw new InvalidOperationException($"agent '{agent.Name}' has no provider configured");

        var llm = await ResolveAsync(agent.ProviderId.Value, ct);

        var providerRow = await _providers.GetByProviderIdAsync(agent.ProviderId.Value, ct);
        var model = agent.ModelOverride ?? providerRow?.DefaultModel ?? "default";

        return (llm, model);
    }
}
