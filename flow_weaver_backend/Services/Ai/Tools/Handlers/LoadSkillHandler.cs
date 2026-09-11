using System.Text.Json;
using flow_weaver_backend.Services.Ai.Skills;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Loads an integration-scoped skill into the conversation. The content comes back
// as the tool result, so the model reads it in this turn; the integration is marked
// on the turn's tool context, so the runner persists it and the skill rides in the
// system prompt of every later turn of this conversation.
//
// Read-level and autonomous: loading the rules for the system the user is asking
// about is part of answering, and has to work for every role.
public sealed class LoadSkillHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string","description":"The skill name, or the integration slug it belongs to, as listed under 'Integration skills' in your instructions."}
        },"required":["name"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly ScopedSkillCatalog _catalog;
    private readonly IToolExecutionContext _turn;

    public LoadSkillHandler(ScopedSkillCatalog catalog, IToolExecutionContext turn)
    {
        _catalog = catalog;
        _turn = turn;
    }

    public string Name => "load_skill";
    public string Description =>
        "Loads the skill for one integration (pagination, filtering and safety rules for its API) " +
        "into this conversation, by skill name or integration slug. Call it before working with a " +
        "system listed under 'Integration skills' in your instructions; once loaded it stays loaded.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = args.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        if (name.Length == 0) return Error("name is required");

        var skills = (await _catalog.ListAsync(ct)).Where(s => !s.AlwaysLoaded).ToList();
        var matches = skills
            .Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || s.IntegrationSlug.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || s.IntegrationName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            var known = skills.Count == 0
                ? "(no integration skills are configured)"
                : string.Join(", ", skills.Select(s => $"{s.Name} (`{s.IntegrationSlug}`)"));
            return Error($"no integration skill matches '{name}'. Available: {known}");
        }

        // A slug can own several skills; they all belong to the same integration, so
        // one call loads the set the runner would have loaded on its own.
        var integrationId = matches[0].IntegrationId;
        var alreadyLoaded = !_turn.MarkSkillLoaded(integrationId);
        var loaded = skills.Where(s => s.IntegrationId == integrationId).ToList();

        return JsonSerializer.SerializeToElement(new
        {
            integration = loaded[0].IntegrationName,
            slug = loaded[0].IntegrationSlug,
            already_loaded = alreadyLoaded,
            skills = loaded.Select(s => new { name = s.Name, title = s.Title, content = s.Content }).ToList(),
            note = "This skill now stays loaded for the rest of the conversation.",
        });
    }

    private static JsonElement Error(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
