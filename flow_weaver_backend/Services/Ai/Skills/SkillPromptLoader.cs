using System.Collections.Concurrent;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;

namespace flow_weaver_backend.Services.Ai.Skills;

// Reads every active GLOBAL ai_prompt_skills row (IntegrationId null) and
// concatenates their Content into the agent's system prompt. Rows are ordered
// by SortOrder (base.md = 0 leads) then Name. Placeholders {{CurrentDate}} and
// {{ToolList}} are substituted from the request-supplied SkillTemplateContext.
//
// Rows tied to an integration are not here: ScopedSkillCatalog indexes them and
// the runner loads their text only when that integration is in play.
// Concatenating them all meant every integration's rules rode in every turn
// about anything else. The shipped domain skills listed in TopicSkillCatalog get
// the same treatment: LoadTopicsAsync hands them to the runner instead.
//
// Registered as a Singleton because the cache must survive between
// requests; DB access is obtained lazily through IServiceScopeFactory so
// we can still resolve the scoped IAiPromptSkillRepository. The cache is
// invalidated explicitly by AiPromptSkillService after every write; as a
// belt-and-suspenders check the stored MAX(UpdatedAt) is compared on
// every load in case the invalidation path is ever missed.
public class SkillPromptLoader : ISkillPromptLoader
{
    private const string SectionSeparator = "\n\n---\n\n";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SkillPromptLoader> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CacheEntry? _cache;

    public SkillPromptLoader(IServiceScopeFactory scopeFactory, ILogger<SkillPromptLoader> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<string> LoadAsync(SkillTemplateContext context, CancellationToken ct = default)
    {
        var combined = (await GetCacheAsync(ct)).Combined;
        return combined
            .Replace("{{CurrentDate}}", context.CurrentDate)
            .Replace("{{ToolList}}", context.ToolList);
    }

    // Rows listed in TopicSkillCatalog, left out of LoadAsync's text. Same cache, so
    // an edit to one shows up in both on the next turn.
    public async Task<IReadOnlyList<TopicSkill>> LoadTopicsAsync(CancellationToken ct = default)
        => (await GetCacheAsync(ct)).Topics;

    public void Invalidate() => _cache = null;

    private async Task<CacheEntry> GetCacheAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Fast path: serve from cache if nothing has changed since the
            // last load.
            using var scope = _scopeFactory.CreateScope();
            var skills = scope.ServiceProvider.GetRequiredService<IAiPromptSkillRepository>();

            var maxUpdated = await skills.MaxActiveUpdatedAtAsync(ct);

            if (_cache is { } entry
                && maxUpdated is not null
                && entry.MaxUpdatedUtc >= maxUpdated.Value)
            {
                return entry;
            }

            var rows = await skills.ListActiveGlobalOrderedAsync(ct);

            var everyTurn = rows.Where(r => !TopicSkillCatalog.IsTopic(r.Name)).Select(r => r.Content).ToList();
            var topics = rows
                .Select(r => TopicSkillCatalog.From(r.Name, r.Content))
                .OfType<TopicSkill>()
                .ToList();

            var combined = string.Join(SectionSeparator, everyTurn);
            _cache = new CacheEntry
            {
                Combined = combined,
                Topics = topics,
                MaxUpdatedUtc = maxUpdated ?? DateTime.MinValue,
            };

            _logger.LogInformation(
                "Loaded {Count} prompt skill row(s) ({Chars} chars) and {TopicCount} on-demand topic skill(s) ({TopicChars} chars)",
                everyTurn.Count, combined.Length, topics.Count, topics.Sum(t => t.Content.Length));

            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class CacheEntry
    {
        public string Combined { get; init; } = string.Empty;
        public IReadOnlyList<TopicSkill> Topics { get; init; } = [];
        public DateTime MaxUpdatedUtc { get; init; }
    }
}
