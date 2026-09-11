using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

public sealed class ListSnippetsHandler : IToolHandler
{
    public string Name => "list_snippets";
    // `proven` tells the model which of these have actually worked. Without it
    // the agent picked by name alone and would happily wire in a half-finished
    // experiment that had never completed a run, when a working equivalent was
    // sitting in the same list.
    //
    // Advisory, not a filter: the agent routinely creates a snippet and uses it
    // in the same turn, and that one is unproven by definition — filtering here
    // would break the flow this tool is most used for.
    public string Description =>
        "List available snippets (ping, rest_call, ssh, transform, etc). Returns id, name, "
        + "type, description and `proven`. proven=true means a step using it has completed "
        + "successfully at least once on this instance; proven=false means it has never "
        + "finished a run, so treat it as a draft. PREFER a proven snippet when several fit; "
        + "results are already ordered proven-first. A snippet you created earlier in this "
        + "turn is unproven and that is expected.";
    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{"type":{"type":"string","description":"Filter by snippet type"}},"additionalProperties":false}
        """).RootElement;

    private readonly ISnippetRepository _snippets;
    private readonly ICurrentUser _caller;
    private readonly ILogger<ListSnippetsHandler> _logger;

    public ListSnippetsHandler(ISnippetRepository snippets, ICurrentUser caller, ILogger<ListSnippetsHandler> logger)
    {
        _snippets = snippets;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var type = args.TryGetProperty("type", out var t) ? t.GetString() : null;
        _logger.LogDebug("ai.tool.list_snippets.start type_filter={TypeFilter}", type);

        try
        {
            var rows = await _snippets.ListActiveCatalogAsync(type, 100, ct);
            var list = rows
                .Select(s => new
                {
                    snippet_id = s.SnippetId, s.Name, s.Type, s.Description,
                    proven = s.Proven,
                })
                .ToList();

            _logger.LogInformation(
                "ai.tool.list_snippets.ok type_filter={TypeFilter} count={Count} proven={Proven}",
                type, list.Count, list.Count(x => x.proven));
            return JsonSerializer.SerializeToElement(list);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.list_snippets.failed type_filter={TypeFilter}", type);
            throw;
        }
    }
}
