namespace flow_weaver_backend.Models;

// One row = one markdown file that used to live under /Skills/*.md. The
// concatenation of every active row (ordered by SortOrder, then Name) is
// the agent's system prompt, served by SkillPromptLoader and rendered at
// /api/ai/catalog/prompt.
//
// Seed: on boot, if the table is still empty, /Skills/*.md on disk is
// imported as the initial dataset. After seed, DB is the source of truth
// and edits happen via /api/admin/prompt-skills (admin-only UI upload).
public class AiPromptSkill : BaseModel
{
    public Guid AiPromptSkillId { get; set; }

    // Stable identifier; matches the original filename (e.g. "base.md")
    // and is unique.
    public string Name { get; set; } = string.Empty;

    // Raw markdown body. Supports the same {{CurrentDate}} / {{ToolList}}
    // placeholders the file-backed loader used.
    public string Content { get; set; } = string.Empty;

    // Concatenation order. base.md should be 0 so it leads the prompt;
    // everything else defaults to 100 and sorts alphabetically within.
    public int SortOrder { get; set; }

    // UserId of the admin who last uploaded/edited this row. Null on seed.
    public Guid? CreatedBy { get; set; }

    // Optional link to an Integration. When set, the agent treats this
    // skill as "scoped to that integration" — meaning the operations it
    // mentions are expected to target the integration's base URL and use
    // its credentials. Null = global skill (unscoped).
    public Guid? IntegrationId { get; set; }
}
