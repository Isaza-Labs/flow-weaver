using flow_weaver_backend.Dtos;

namespace flow_weaver_backend.Services.Interfaces;

// Assembles the agent's system prompt by reading the ai_prompt_skills
// rows, ordering them by SortOrder+Name, concatenating with a markdown
// separator, and substituting {{CurrentDate}} and {{ToolList}}
// placeholders. Caches the result and only re-queries when a mutation
// hits Invalidate or when the row-level MAX(UpdatedAt) has advanced.
public interface ISkillPromptLoader
{
    // Builds (or returns cached) prompt for the supplied context.
    Task<string> LoadAsync(SkillTemplateContext context, CancellationToken ct = default);

    // Drops the cached prompt. Called by AiPromptSkillService after every
    // insert/update/delete so the next LoadAsync sees the fresh state
    // without waiting for the mtime-style heuristic.
    void Invalidate();
}
