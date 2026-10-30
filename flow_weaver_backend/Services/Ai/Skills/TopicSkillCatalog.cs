using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace flow_weaver_backend.Services.Ai.Skills;

// A global skill row that is loaded on demand: Name is the row name (the shipped
// file name), LoadKey the id it is remembered by in a conversation's loaded set.
public sealed record TopicSkill(string Name, string Summary, string Content, Guid LoadKey);

// The shipped global skills that ride only when their topic is in play.
//
// Every global row used to go into every turn: about 200 KB of Markdown, some 55k
// tokens, re-sent on every tool round of every turn whether the conversation was
// about building a workflow or about who is logged in. Most of it documents one
// area each — the workflow model, SSH, git, email — so it gets the scheme the
// integration skills already have (ScopedSkillCatalog): one index line per topic in
// the prompt, the text only when the topic is in play, which is when
//
//   1. the user's message names it (the keywords below, English and Spanish);
//   2. it was loaded earlier in the same conversation (the same persisted set as
//      integration skills; a topic's key is derived from its name);
//   3. one of its tools is called (the text lands right behind that call's result,
//      so the next call is made with the rules in view);
//   4. the model asks for it with load_skill.
//
// What stays in every turn: base.md, the two skills that shape any request
// (clarification.md — intake before planning; discovery.md — calling APIs in the
// fewest round-trips), and every row not listed here, an operator's own included:
// nothing here knows what those cover, and dropping them would be a silent change.
//
// Keyed by row name, which is the file name the seeder gave it, so an operator who
// edits a shipped row keeps it on demand with their own text.
public static class TopicSkillCatalog
{
    private sealed record Definition(string Summary, Regex? Mentions, FrozenSet<string> Tools);

    private static Definition Def(string summary, string[] keywords, params string[] tools)
        => new(
            summary,
            keywords.Length == 0
                ? null
                // Each keyword matches at the start of a word, so "workflow" also finds
                // "workflows" and "aprobaci" finds "aprobación"; it never matches inside
                // a word ("rol" does not fire on "control"). A trailing `$` asks for the
                // whole word, for the short ones that begin longer unrelated words
                // ("repo$" must not fire on "report", "cli$" on "client").
                : new Regex(
                    @"(?<![\p{L}\p{N}_])(?:" + string.Join("|", keywords.Select(Pattern)) + ")",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            tools.ToFrozenSet(StringComparer.OrdinalIgnoreCase));

    private static string Pattern(string keyword)
        => keyword.EndsWith('$')
            ? Regex.Escape(keyword[..^1]) + @"(?![\p{L}\p{N}_])"
            : Regex.Escape(keyword);

    private static readonly FrozenDictionary<string, Definition> Definitions =
        new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase)
        {
            ["workflows.md"] = Def(
                "designing, building, editing and promoting workflows — plan, nodes, config, triggers, ready (the canonical shapes)",
                ["workflow", "flujo", "automatiz", "automation", "automate", "playbook", "pipeline", "orquest", "orchestrat"],
                "evaluate_prompt_sufficiency", "create_workflow_plan", "update_workflow_node_config",
                "mark_workflow_ready", "simulate_workflow_run", "run_acceptance_tests", "analyze_foreign_workflow"),
            ["snippets.md"] = Def(
                "snippet types and the decision ladder for which step type a workflow node should be",
                ["snippet", "workflow", "flujo", "automatiz", "step type", "tipo de paso"],
                "create_snippet", "generate_snippet_for_import", "create_workflow_plan", "update_workflow_node_config"),
            ["python_snippets.md"] = Def(
                "writing `python_snippet` code: sandbox, allowed imports, secrets, calling registered integrations",
                ["python", "script", "pandas"],
                "create_snippet", "generate_snippet_for_import"),
            ["netmiko_snippets.md"] = Def(
                "network-enabled python snippets with netmiko/paramiko: interactive SSH sessions, porting existing scripts",
                ["netmiko", "paramiko", "napalm", "interactive ssh", "ssh interactiv"]),
            ["mermaid.md"] = Def(
                "authoring the `logic_diagram_mermaid` field of workflows and snippets",
                ["mermaid", "diagram", "diagrama", "workflow", "flujo", "snippet"],
                "evaluate_prompt_sufficiency", "create_workflow_plan", "create_snippet"),
            ["integrations.md"] = Def(
                "registered integrations in workflows: prefer `integration_action` nodes over hand-rolled python",
                ["integration", "integraci", "integration_action", "external api", "api externa"],
                "create_workflow_plan", "create_snippet"),
            ["ssh.md"] = Def(
                "running commands on network devices over SSH: Netmiko platforms, `ssh` nodes, vendor commands, command validation",
                ["ssh", "netmiko", "cli$", "command line", "línea de comandos", "router", "switch", "firewall",
                 "comando", "command", "show run", "show ver"],
                "validate_ssh_commands", "find_command", "list_vendor_commands", "create_vendor_command",
                "update_vendor_command", "delete_vendor_command"),
            ["git.md"] = Def(
                "reading, editing, committing and pushing files in registered git repositories; the `git` workflow node",
                ["git", "github", "gitlab", "bitbucket", "repo$", "repos$", "repositor", "commit", "push",
                 "branch", "rama$", "ramas$", "pull request"],
                "git_commit_push", "git_create_remote_repository", "git_create_webhook", "git_diff", "git_list_files",
                "git_list_repositories", "git_list_webhooks", "git_pull", "git_read_file", "git_write_file"),
            ["email.md"] = Def(
                "sending email from workflows and the chat: SMTP channels, `email_send` nodes, attachments",
                ["email", "e-mail", "correo", "mail", "smtp", "adjunt", "attachment"]),
            ["slack_integration.md"] = Def(
                "sending Slack messages from a workflow",
                ["slack"]),
            ["reports.md"] = Def(
                "generating reports: the `report` snippet, report artifacts, sharing them",
                ["report", "informe", "reporte", "dashboard"],
                "generate_report"),
            ["runs.md"] = Def(
                "runs: reading run details and step logs, following an execution",
                ["runs", "run_id", "ejecuci", "execution", "corrida", "step log"],
                "get_run_details", "get_step_logs"),
            ["troubleshooting.md"] = Def(
                "diagnosing failures: the errors that actually happen and what each one means",
                ["error", "falla", "fallo", "falló", "fail", "troubleshoot", "problema", "debug", "no funciona", "broken", "roto"],
                "get_run_details", "get_step_logs"),
            ["governance.md"] = Def(
                "governance: plans, approvals, policies, permissions, audit",
                ["govern", "gobern", "aprobaci", "aprobar", "approv", "policy", "policies", "polític", "audit",
                 "permis", "permission", "rbac", "role", "rol$", "roles$"],
                "create_policy", "list_policies", "list_plan_features", "grant_resource_permission",
                "set_user_role", "create_user", "list_users"),
            ["inventory.md"] = Def(
                "inventory: devices, pools, sources, credentials",
                ["inventor", "device", "dispositiv", "pool", "credential", "credencial"],
                "query_devices", "list_credentials"),
            ["triggers.md"] = Def(
                "workflow triggers: schedules, cron, webhooks, target devices",
                ["trigger", "disparador", "schedule", "programar", "programad", "programaci", "cron", "webhook",
                 "daily", "hourly", "weekly", "diari", "cada día", "cada hora", "cada semana"]),
            ["mcp.md"] = Def(
                "calling external Model Context Protocol (MCP) servers and their tools",
                ["mcp", "model context protocol"],
                "list_mcp_servers", "discover_mcp_tools", "call_mcp_tool"),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool IsTopic(string name) => Definitions.ContainsKey(name);

    // The topic for a row, or null when the row is not one of the on-demand skills.
    public static TopicSkill? From(string name, string content)
        => Definitions.TryGetValue(name, out var d) ? new TopicSkill(name, d.Summary, content, LoadKeyFor(name)) : null;

    // A topic's id in a conversation's loaded-skill set, which already holds
    // integration ids. Derived from the name so it survives reseeds and row
    // recreation, and the same name always means the same key.
    public static Guid LoadKeyFor(string name)
        => new(MD5.HashData(Encoding.UTF8.GetBytes("topic-skill:" + name.Trim().ToLowerInvariant())));

    // Topics whose keywords appear in the text.
    public static IReadOnlySet<Guid> MatchMentions(string? text, IReadOnlyList<TopicSkill> topics)
    {
        var hits = new HashSet<Guid>();
        if (string.IsNullOrWhiteSpace(text)) return hits;
        foreach (var t in topics)
            if (Definitions.TryGetValue(t.Name, out var d) && d.Mentions?.IsMatch(text) == true)
                hits.Add(t.LoadKey);
        return hits;
    }

    // Topics a call to this tool puts in play.
    public static IReadOnlyList<TopicSkill> ForTool(string toolName, IReadOnlyList<TopicSkill> topics)
        => topics.Where(t => Definitions.TryGetValue(t.Name, out var d) && d.Tools.Contains(toolName)).ToList();

    // The topic a load_skill argument names: "workflows.md", "workflows" or
    // "Skills/workflows.md", the three ways the skills refer to one another.
    public static TopicSkill? Find(string name, IReadOnlyList<TopicSkill> topics)
    {
        var key = name.Trim().Replace('\\', '/');
        if (key.StartsWith("Skills/", StringComparison.OrdinalIgnoreCase)) key = key["Skills/".Length..];
        if (!key.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) key += ".md";
        return topics.FirstOrDefault(t => t.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    // The topic part of the prompt: the index, then the text of every loaded topic.
    // Empty when there are none.
    public static string Render(IReadOnlyList<TopicSkill> topics, IReadOnlySet<Guid> loaded)
    {
        if (topics.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("## Topic skills\n\n");
        sb.Append("Detailed guides, one per area, loaded only when their topic is in play so this prompt ");
        sb.Append("stays small. A reference elsewhere in your instructions to `Skills/<name>.md` or ");
        sb.Append("`<name>.md` points at one of these. Available:\n\n");
        foreach (var t in topics)
        {
            var state = loaded.Contains(t.LoadKey) ? "loaded — its text follows below" : "not loaded";
            sb.Append("- `").Append(t.Name).Append("`: ").Append(t.Summary).Append(" — ").Append(state).Append('\n');
        }
        sb.Append('\n');
        sb.Append("A topic skill loads by itself when the user's message names its topic or when you call one ");
        sb.Append("of its tools, and stays loaded for the rest of the conversation. Before you work in one of ");
        sb.Append("these areas — above all before creating or changing anything — load its skill with ");
        sb.Append("`load_skill` if it is not loaded yet: it holds the rules and shapes that area expects, and ");
        sb.Append("work done without it tends to be wrong.");

        foreach (var t in topics)
            if (loaded.Contains(t.LoadKey))
                sb.Append("\n\n---\n\n").Append(RenderLoaded(t));
        return sb.ToString();
    }

    public static string RenderLoaded(TopicSkill t) => $"[Topic skill `{t.Name}`]\n\n{t.Content}";
}
