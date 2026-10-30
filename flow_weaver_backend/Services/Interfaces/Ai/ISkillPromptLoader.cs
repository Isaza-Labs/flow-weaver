using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Skills;

namespace flow_weaver_backend.Services.Interfaces;

// Assembles the agent's system prompt by reading the ai_prompt_skills
// rows, ordering them by SortOrder+Name, concatenating with a markdown
// separator, and substituting {{CurrentDate}} and {{ToolList}}
// placeholders. Caches the result and only re-queries when a mutation
// hits Invalidate or when the row-level MAX(UpdatedAt) has advanced.
public interface ISkillPromptLoader
{
    // Builds (or returns cached) prompt for the supplied context. Topic skills
    // (TopicSkillCatalog) are not in it; they come from LoadTopicsAsync.
    Task<string> LoadAsync(SkillTemplateContext context, CancellationToken ct = default);

    // The global rows that are loaded on demand, in prompt order. Empty by default
    // so a loader that knows nothing about topics keeps every row in the prompt.
    Task<IReadOnlyList<TopicSkill>> LoadTopicsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TopicSkill>>([]);

    // Drops the cached prompt. Called by AiPromptSkillService after every
    // insert/update/delete so the next LoadAsync sees the fresh state
    // without waiting for the mtime-style heuristic.
    void Invalidate();
}
