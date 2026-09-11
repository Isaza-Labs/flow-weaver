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
// about anything else.
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
        var combined = await GetCombinedAsync(ct);
        return combined
            .Replace("{{CurrentDate}}", context.CurrentDate)
            .Replace("{{ToolList}}", context.ToolList);
    }

    public void Invalidate() => _cache = null;

    private async Task<string> GetCombinedAsync(CancellationToken ct)
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
                return entry.Combined;
            }

            var rows = await skills.ListActiveGlobalContentsOrderedAsync(ct);

            var combined = string.Join(SectionSeparator, rows);
            _cache = new CacheEntry
            {
                Combined = combined,
                MaxUpdatedUtc = maxUpdated ?? DateTime.MinValue,
            };

            _logger.LogInformation(
                "Loaded {Count} prompt skill row(s) ({Chars} chars)",
                rows.Count, combined.Length);

            return combined;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class CacheEntry
    {
        public string Combined { get; init; } = string.Empty;
        public DateTime MaxUpdatedUtc { get; init; }
    }
}
