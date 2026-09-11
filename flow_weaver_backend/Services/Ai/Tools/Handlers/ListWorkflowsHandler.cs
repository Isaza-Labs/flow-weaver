using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

public sealed class ListWorkflowsHandler : IToolHandler
{
    public string Name => "list_workflows";
    public string Description => "List all workflows. Returns id, name, environment, version, and the page url.";
    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{"environment":{"type":"string","description":"Filter by environment (draft/qa/production)"}},"additionalProperties":false}
        """).RootElement;

    private readonly IWorkflowRepository _workflows;
    private readonly ICurrentUser _caller;
    private readonly IAppLinks _links;
    private readonly ILogger<ListWorkflowsHandler> _logger;

    public ListWorkflowsHandler(
        IWorkflowRepository workflows,
        ICurrentUser caller,
        IAppLinks links,
        ILogger<ListWorkflowsHandler> logger)
    {
        _workflows = workflows;
        _caller = caller;
        _links = links;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var env = args.TryGetProperty("environment", out var e) ? e.GetString() : null;
        _logger.LogDebug("ai.tool.list_workflows.start environment={Environment}", env);

        try
        {
            var list = await _workflows.ListSummariesAsync(env, 50, ct);

            _logger.LogInformation(
                "ai.tool.list_workflows.ok environment={Environment} count={Count}",
                env, list.Count);
            // Spread of WorkflowSummary + the page url. Property names are
            // repeated verbatim so the shape the model already knows doesn't
            // shift (the record serializes PascalCase, unlike the snake_case
            // anonymous objects elsewhere in this folder).
            return JsonSerializer.SerializeToElement(
                list.Select(w => new
                {
                    w.WorkflowId,
                    w.Name,
                    w.Environment,
                    w.Version,
                    Url = _links.Workflow(w.WorkflowId),
                }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.list_workflows.failed environment={Environment}", env);
            throw;
        }
    }
}
