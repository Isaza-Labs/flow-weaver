using System.Text;
using System.Text.Json;
using flow_weaver_backend.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SdkClient = ModelContextProtocol.Client.McpClient;

namespace flow_weaver_backend.Services.Mcp;

public sealed class McpClient : IMcpClient
{
    // Per-connection handshake budget (SDK initialize) and a per-operation
    // ceiling. The named HttpClient uses an infinite timeout so SSE streaming
    // isn't cut short — these linked-CTS budgets are the real limits.
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    // Safety cap on the text returned to a caller (the agent path also caps
    // centrally for the LLM; a step output is bounded here).
    private const int MaxContentChars = 100_000;

    private static readonly McpClientOptions ClientOptions = new()
    {
        ClientInfo = new Implementation { Name = "flow-weaver", Version = "1.0" },
    };

    private readonly IMcpConnectionFactory _factory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;

    public McpClient(
        IMcpConnectionFactory factory,
        IHttpClientFactory httpFactory,
        ILoggerFactory loggerFactory)
    {
        _factory = factory;
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
    }

    public Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(McpServer server, CancellationToken ct = default)
        => WithClientAsync(server, async (client, opCt) =>
        {
            var tools = await client.ListToolsAsync((RequestOptions?)null, opCt);
            // Clone the schema out of the SDK's document — it must outlive the session.
            IReadOnlyList<McpToolDescriptor> result = tools
                .Select(t => new McpToolDescriptor(t.Name, t.Title, t.Description, t.JsonSchema.Clone()))
                .ToList();
            return result;
        }, ct);

    public Task<McpCallResult> CallToolAsync(
        McpServer server, string toolName, JsonElement arguments, CancellationToken ct = default)
        => WithClientAsync(server, async (client, opCt) =>
        {
            var callParams = new CallToolRequestParams { Name = toolName, Arguments = ToArguments(arguments) };
            var result = await client.CallToolAsync(callParams, opCt);
            return Normalize(result);
        }, ct);

    private async Task<T> WithClientAsync<T>(
        McpServer server, Func<SdkClient, CancellationToken, Task<T>> op, CancellationToken ct)
    {
        var conn = await _factory.CreateAsync(server, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(OperationTimeout);

        var httpClient = _httpFactory.CreateClient(
            conn.TlsSkipVerify ? McpHttpClients.Insecure : McpHttpClients.Secure);

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = conn.Endpoint,
            TransportMode = HttpTransportMode.AutoDetect,
            ConnectionTimeout = ConnectionTimeout,
            Name = server.Name,
        };
        if (conn.Headers.Count > 0)
            transportOptions.AdditionalHeaders = conn.Headers;

        var transport = new HttpClientTransport(transportOptions, httpClient, _loggerFactory, ownsHttpClient: false);

        SdkClient client;
        try
        {
            client = await SdkClient.CreateAsync(transport, ClientOptions, _loggerFactory, cts.Token);
        }
        catch
        {
            // CreateAsync failed (unreachable / auth / initialize), so the client
            // never took ownership of the transport — dispose it ourselves.
            await transport.DisposeAsync();
            throw;
        }

        await using (client)
        {
            return await op(client, cts.Token);
        }
    }

    internal static McpCallResult Normalize(CallToolResult result)
    {
        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(text.Text);
            }
        }

        var content = sb.ToString();
        if (content.Length > MaxContentChars)
            content = content[..MaxContentChars] + "\n…(truncated)";

        JsonElement? structured = result.StructuredContent is { } s ? s.Clone() : null;
        return new McpCallResult(content, structured, result.IsError ?? false);
    }

    internal static Dictionary<string, JsonElement>? ToArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in arguments.EnumerateObject())
            dict[p.Name] = p.Value;
        return dict;
    }
}
