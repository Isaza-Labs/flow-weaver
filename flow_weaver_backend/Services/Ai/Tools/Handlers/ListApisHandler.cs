using System.Text.Json;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Lists every OpenAPI spec loaded. Entry point for the
// netora-style discover → detail → execute flow: the agent calls this
// first to learn which APIs it can talk to.
public sealed class ListApisHandler : IToolHandler
{
    public string Name => "list_apis";
    public string Description =>
        "Lists the available REST APIs. Each entry has an `api` " +
        "identifier (use it as the `api` argument in `discover_operations`), an " +
        "operation count, and the tag set. Call this before `discover_operations` " +
        "when you don't yet know which API to target.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{},"additionalProperties":false}
        """).RootElement;

    private readonly IApiSpecIndex _index;
    private readonly ICurrentUser _caller;
    private readonly ILogger<ListApisHandler> _logger;

    public ListApisHandler(IApiSpecIndex index, ICurrentUser caller, ILogger<ListApisHandler> logger)
    {
        _index = index;
        _caller = caller;
        _logger = logger;
    }

    public Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        _logger.LogDebug("ai.tool.list_apis.start");

        try
        {
            var all = _index.All();
            var grouped = all
                .GroupBy(op => op.Api, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    api = g.Key,
                    operation_count = g.Count(),
                    tags = g.SelectMany(o => o.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToList(),
                })
                .OrderBy(x => x.api)
                .ToList();

            _logger.LogInformation("ai.tool.list_apis.ok api_count={ApiCount}", grouped.Count);
            return Task.FromResult(JsonSerializer.SerializeToElement(new { apis = grouped }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.list_apis.failed");
            throw;
        }
    }
}
