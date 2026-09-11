using flow_weaver_backend.Services.Ai.Providers;

namespace flow_weaver_backend.Services.Ai.Tools;

// Singleton registry that holds the tool metadata (name, description,
// parameter schema) for all registered IToolHandlers. The actual handler
// instances are Scoped — the registry only stores metadata + the Type
// so the dispatcher can resolve a fresh instance per request.
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolEntry> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ToolRegistry> _logger;

    public ToolRegistry(ILogger<ToolRegistry> logger)
    {
        _logger = logger;
    }

    public void Register(IToolHandler handler)
    {
        _tools[handler.Name] = new ToolEntry(handler.Name, handler.Description,
            handler.ParametersSchema, handler.GetType());
        _logger.LogDebug(
            "ai.tool.registry.register tool={Tool} handler_type={HandlerType}",
            handler.Name, handler.GetType().Name);
    }

    public ToolEntry? Get(string name)
    {
        if (_tools.TryGetValue(name, out var entry))
        {
            _logger.LogDebug("ai.tool.registry.lookup.hit tool={Tool}", name);
            return entry;
        }

        _logger.LogWarning("ai.tool.registry.lookup.miss tool={Tool}", name);
        return null;
    }

    public IReadOnlyCollection<ToolEntry> All => _tools.Values;

    public List<ToolDefinition> ToDefinitions(IEnumerable<string>? allowedNames = null)
    {
        var source = allowedNames is not null
            ? _tools.Values.Where(t => allowedNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
            : _tools.Values;

        return source.Select(t => new ToolDefinition
        {
            Name = t.Name,
            Description = t.Description,
            ParametersSchema = t.Schema,
        }).ToList();
    }
}

public sealed record ToolEntry(
    string Name,
    string Description,
    System.Text.Json.JsonElement Schema,
    Type HandlerType);
