namespace flow_weaver_backend.Services.Ai.Providers;

public interface ILlmProvider
{
    string ProviderType { get; }
    Task<ChatResult> ChatAsync(
        List<LlmMessage> messages, string model, double temperature, CancellationToken ct);
}

public interface IToolCallingLlmProvider : ILlmProvider
{
    Task<ChatResult> ChatWithToolsAsync(
        List<LlmMessage> messages,
        List<ToolDefinition> tools,
        string model,
        double temperature,
        CancellationToken ct);
}

public interface IStreamingToolCallingLlmProvider : IToolCallingLlmProvider
{
    IAsyncEnumerable<ChatStreamEvent> ChatWithToolsStreamAsync(
        List<LlmMessage> messages,
        List<ToolDefinition> tools,
        string model,
        double temperature,
        CancellationToken ct);
}
