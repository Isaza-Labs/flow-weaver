using System.Text.Json;
using flow_weaver_backend.Services.Ai.Skills;
using flow_weaver_backend.Services.Interfaces;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Loads an on-demand skill into the conversation: a topic skill (TopicSkillCatalog,
// by file name) or the skills of one integration (by skill name or integration
// slug). The content comes back as the tool result, so the model reads it in this
// turn; the load is marked on the turn's tool context, so the runner persists it
// and the skill rides in the system prompt of every later turn of this
// conversation.
//
// Read-level and autonomous: loading the rules for the area the user is asking
// about is part of answering, and has to work for every role.
public sealed class LoadSkillHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string","description":"The skill to load, as listed under 'Topic skills' (e.g. `workflows.md`) or 'Integration skills' (skill name or integration slug) in your instructions."}
        },"required":["name"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly ScopedSkillCatalog _catalog;
    private readonly ISkillPromptLoader _loader;
    private readonly IToolExecutionContext _turn;

    public LoadSkillHandler(ScopedSkillCatalog catalog, ISkillPromptLoader loader, IToolExecutionContext turn)
    {
        _catalog = catalog;
        _loader = loader;
        _turn = turn;
    }

    public string Name => "load_skill";
    public string Description =>
        "Loads an on-demand skill into this conversation: a topic skill (the rules and shapes of one " +
        "area, e.g. `workflows.md`, `git.md`) or an integration's skill (pagination, filtering and " +
        "safety rules for its API, by skill name or integration slug). Call it before working in an " +
        "area listed under 'Topic skills' or 'Integration skills' in your instructions; once loaded " +
        "it stays loaded.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = args.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        if (name.Length == 0) return Error("name is required");

        var topics = await _loader.LoadTopicsAsync(ct);
        if (TopicSkillCatalog.Find(name, topics) is { } topic)
        {
            var already = !_turn.MarkSkillLoaded(topic.LoadKey);
            return JsonSerializer.SerializeToElement(new
            {
                topic = topic.Name,
                already_loaded = already,
                content = topic.Content,
                note = "This skill now stays loaded for the rest of the conversation.",
            });
        }

        var skills = (await _catalog.ListAsync(ct)).Where(s => !s.AlwaysLoaded).ToList();
        var matches = skills
            .Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || s.IntegrationSlug.Equals(name, StringComparison.OrdinalIgnoreCase)
                        || s.IntegrationName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            var known = topics.Select(t => $"`{t.Name}`")
                .Concat(skills.Select(s => $"{s.Name} (`{s.IntegrationSlug}`)"))
                .ToList();
            return Error($"no skill matches '{name}'. Available: "
                + (known.Count == 0 ? "(no on-demand skills are configured)" : string.Join(", ", known)));
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
