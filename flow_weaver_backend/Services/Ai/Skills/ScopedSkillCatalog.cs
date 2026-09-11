using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Services.Ai.Skills;

// One AiPromptSkill row that is tied to an integration. `AlwaysLoaded` marks a row
// whose integration no longer exists or is inactive: it was scoped to something
// that is gone, so it is treated as global — loaded every turn, exactly as every
// row was before scoping existed — rather than silently vanishing from the prompt.
public sealed record ScopedSkill(
    Guid SkillId,
    string Name,
    string Title,
    Guid IntegrationId,
    string IntegrationName,
    string IntegrationSlug,
    string Content,
    int SortOrder,
    bool AlwaysLoaded);

// The skills that ride with an integration, and the two-level scheme that keeps
// them out of the prompt until they are needed.
//
// Every skill used to go into every turn. That is fine for the shipped catalog and
// for rows that describe house style, and wrong for the ones that document a
// single system: an Action1 skill is pages of pagination and safety rules that a
// conversation about NetBox pays for on every round-trip, and each integration
// added costs the same again, forever. So a skill with an IntegrationId is not
// concatenated by SkillPromptLoader any more. Instead:
//
//   1. The prompt always carries an INDEX — one line per scoped skill naming the
//      integration and what the skill covers — so the model knows the knowledge
//      exists even when nobody says the system's name.
//   2. The full text enters when the integration is in play: named in the user's
//      message, loaded earlier in the same conversation (persisted in the
//      conversation's Context), called through execute_operation /
//      operation_detail / discover_operations against its API (auto-load,
//      mid-turn), or asked for with the load_skill tool.
//
// Scoped, not singleton: it reads through the request's AppDbContext. The two
// queries it runs per turn are small and indexed; caching them would only mean a
// second invalidation path to keep in step with the loader's.
public sealed class ScopedSkillCatalog
{
    // Key under AIConversation.Context holding the integration ids whose skills
    // this conversation has loaded. Context was reserved and never written before;
    // this is its first writer.
    public const string ContextKey = "loaded_skills";

    private readonly AppDbContext _db;
    private readonly IApiSpecIndex _specs;

    public ScopedSkillCatalog(AppDbContext db, IApiSpecIndex specs)
    {
        _db = db;
        _specs = specs;
    }

    // Active skills with an IntegrationId, joined to their integration. Ordered as
    // the loader orders global rows (sort order, then name) so a loaded skill lands
    // in the prompt where it would have landed before scoping.
    public async Task<IReadOnlyList<ScopedSkill>> ListAsync(CancellationToken ct)
    {
        var rows = await _db.AiPromptSkills.AsNoTracking()
            .Where(s => s.IsActive && s.IntegrationId != null)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .Select(s => new { s.AiPromptSkillId, s.Name, s.Content, s.SortOrder, IntegrationId = s.IntegrationId!.Value })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var ids = rows.Select(r => r.IntegrationId).Distinct().ToList();
        var integrations = await _db.Integrations.AsNoTracking()
            .Where(i => ids.Contains(i.IntegrationId) && i.IsActive)
            .Select(i => new { i.IntegrationId, i.Name, i.Slug })
            .ToDictionaryAsync(i => i.IntegrationId, ct);

        return rows.Select(r => integrations.TryGetValue(r.IntegrationId, out var i)
                ? new ScopedSkill(r.AiPromptSkillId, r.Name, TitleOf(r.Content, r.Name), r.IntegrationId,
                    i.Name, i.Slug ?? string.Empty, r.Content, r.SortOrder, AlwaysLoaded: false)
                : new ScopedSkill(r.AiPromptSkillId, r.Name, TitleOf(r.Content, r.Name), r.IntegrationId,
                    string.Empty, string.Empty, r.Content, r.SortOrder, AlwaysLoaded: true))
            .ToList();
    }

    // The integration a tool call is about, when the call names an API operation or
    // an API. Null for every other tool and for APIs not bound to an integration.
    // The spec index owns operation_id → api; the spec row owns api → integration.
    public async Task<Guid?> IntegrationForToolCallAsync(string toolName, JsonElement args, CancellationToken ct)
    {
        string? api = null;
        switch (toolName.ToLowerInvariant())
        {
            case "execute_operation":
            case "operation_detail":
                if (Str(args, "operation_id") is { Length: > 0 } operationId)
                    api = _specs.GetByOperationId(operationId)?.Api;
                break;
            case "discover_operations":
                api = Str(args, "api");
                break;
        }
        if (string.IsNullOrWhiteSpace(api)) return null;

        return await _db.AiApiSpecs.AsNoTracking()
            .Where(s => s.Api == api && s.IsActive && s.IntegrationId != null)
            .Select(s => s.IntegrationId)
            .FirstOrDefaultAsync(ct);
    }

    // Integrations whose skill, integration name or slug appears in the text. A
    // three-character floor keeps a slug like "nb" from matching every sentence
    // with "number" in it; a real name is longer than that.
    public static IReadOnlySet<Guid> MatchMentions(string? text, IReadOnlyList<ScopedSkill> skills)
    {
        var hits = new HashSet<Guid>();
        if (string.IsNullOrWhiteSpace(text)) return hits;
        foreach (var s in skills)
        {
            if (s.AlwaysLoaded) continue;
            if (Mentions(text, s.IntegrationSlug) || Mentions(text, s.IntegrationName) || Mentions(text, s.Name))
                hits.Add(s.IntegrationId);
        }
        return hits;
    }

    private static bool Mentions(string text, string needle)
        => needle.Trim().Length >= 3 && text.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);

    // The first Markdown heading, which is how skills introduce themselves
    // ("# Action1 — Patch Management & RMM"); the row name when there is none.
    public static string TitleOf(string content, string fallback)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                var title = line.TrimStart('#').Trim();
                if (title.Length > 0) return title;
            }
        }
        return fallback;
    }

    // The integration ids recorded in a conversation's Context, if any.
    public static IReadOnlyCollection<Guid> LoadedFromContext(JsonElement context)
    {
        if (context.ValueKind != JsonValueKind.Object
            || !context.TryGetProperty(ContextKey, out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            return [];
        var ids = new List<Guid>();
        foreach (var el in arr.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && Guid.TryParse(el.GetString(), out var id))
                ids.Add(id);
        return ids;
    }

    // The conversation's Context with `loaded_skills` set, every other key kept.
    public static JsonElement ContextWithLoaded(JsonElement context, IEnumerable<Guid> loaded)
    {
        var dict = new Dictionary<string, object?>();
        if (context.ValueKind == JsonValueKind.Object)
            foreach (var p in context.EnumerateObject())
                if (p.Name != ContextKey) dict[p.Name] = p.Value.Clone();
        dict[ContextKey] = loaded.Distinct().Select(g => g.ToString()).ToList();
        return JsonSerializer.SerializeToElement(dict);
    }

    // The system prompt for a turn: the loader's text, then the index, then the full
    // text of every skill that is loaded — orphaned rows always, the rest by id.
    public static string ComposePrompt(string basePrompt, IReadOnlyList<ScopedSkill> skills, IReadOnlySet<Guid> loaded)
    {
        if (skills.Count == 0) return basePrompt;

        var sb = new StringBuilder(basePrompt.TrimEnd());
        sb.Append("\n\n---\n\n").Append(RenderIndex(skills, loaded));
        foreach (var s in skills)
        {
            if (!s.AlwaysLoaded && !loaded.Contains(s.IntegrationId)) continue;
            sb.Append("\n\n---\n\n").Append(RenderLoaded(s));
        }
        return sb.ToString();
    }

    // What the model reads about skills it does NOT have yet. Written for the model:
    // it says how a skill gets loaded and why to load it before calling the API.
    public static string RenderIndex(IReadOnlyList<ScopedSkill> skills, IReadOnlySet<Guid> loaded)
    {
        var listed = skills.Where(s => !s.AlwaysLoaded).ToList();
        if (listed.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("## Integration skills\n\n");
        sb.Append("Knowledge specific to one integration lives in a skill that is loaded only when that ");
        sb.Append("integration is in play, so this prompt stays small. Available:\n\n");
        foreach (var s in listed)
        {
            var state = loaded.Contains(s.IntegrationId) ? "loaded — its text follows below" : "not loaded";
            sb.Append("- `").Append(s.Name).Append("` → integration \"").Append(s.IntegrationName)
              .Append("\" (`").Append(s.IntegrationSlug).Append("`): ").Append(s.Title)
              .Append(" — ").Append(state).Append('\n');
        }
        sb.Append('\n');
        sb.Append("A skill loads by itself the first time you call `execute_operation`, `operation_detail` ");
        sb.Append("or `discover_operations` against that integration's API, and it stays loaded for the rest ");
        sb.Append("of the conversation. When the user's request concerns one of these systems, load its skill ");
        sb.Append("FIRST with `load_skill` (by skill name or integration slug): it holds the pagination, ");
        sb.Append("filtering and safety rules for that API, and calls made without it tend to be wrong.");
        return sb.ToString();
    }

    // A loaded skill, labelled so the model can tell which integration it governs.
    public static string RenderLoaded(ScopedSkill s)
        => s.AlwaysLoaded
            ? s.Content
            : $"[Integration skill `{s.Name}` — applies to integration \"{s.IntegrationName}\" (`{s.IntegrationSlug}`)]\n\n{s.Content}";

    private static string? Str(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
