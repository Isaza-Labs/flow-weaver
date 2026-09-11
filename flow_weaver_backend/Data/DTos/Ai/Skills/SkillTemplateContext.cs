namespace flow_weaver_backend.Dtos;

// Substitution values applied to skill prompt templates (.md files) when the loader
// concatenates them into the final system prompt.
//   - {{CurrentDate}} → CurrentDate (YYYY-MM-DD, UTC)
//   - {{ToolList}}    → ToolList (human-readable list of available tools)
//
// ToolList is intentionally left empty by default; callers override it with the
// tool registry's rendered list once it is built.
public class SkillTemplateContext
{
    public string CurrentDate { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd");

    public string ToolList { get; set; } = string.Empty;

}
