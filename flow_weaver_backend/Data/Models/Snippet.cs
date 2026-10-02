using System.Text.Json;

namespace flow_weaver_backend.Models;

// Reusable building block that a workflow node invokes: ping, rest_call,
// python_snippet, ansible_playbook, transform, jmespath, ssh, netconf,
// snmp_v3, integration_action. The `Type` column picks which handler the
// worker dispatches to; `Code` carries the script/template body when the
// handler needs one (python_snippet, ansible_playbook, transform,
// jmespath).
public class Snippet : BaseModel
{
    public Guid SnippetId { get; set; }
    public string Name { get; set; } = string.Empty;

    // Stable cross-instance identity — see Integration.Slug. A workflow bundle
    // embeds each referenced snippet's full definition keyed by this, so an
    // import can tell "the snippet you already have" from "one I must create".
    public string? Slug { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JsonElement InputSchema { get; set; } = default;
    public JsonElement OutputSchema { get; set; } = default;
    public string? Code { get; set; }
    public string? ScriptLanguage { get; set; }
    public string TargetMode { get; set; } = string.Empty;
    public int MaxParallel { get; set; }
    public int TimeoutSeconds { get; set; }
    public bool Verified { get; set; }
    public JsonElement RetryPolicy { get; set; } = default;
    public string? CreatedBy { get; set; }

    // Explicit override of the handler's IdempotencyKind. When
    // null the handler's DefaultIdempotency applies. Authors set this
    // when they know the snippet is, for example, a verifiably read-only
    // integration_action even though the handler defaults to
    // RequiresCompensation. The promotion gate caps the override at the
    // handler's default — a snippet cannot weaken below the handler's
    // floor (a NonReversible handler stays NonReversible regardless).
    //
    // Stored as a string ("idempotent" | "requires_compensation" |
    // "non_reversible") so future kinds don't require a migration.
    public string? Idempotency { get; set; }

    /// <summary>
    /// Whether a step running this snippet changes anything. Null when the author has not
    /// said, which is only legal for types whose handler CAN say.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="Idempotency"/>, and a different question: that one says
    /// whether the action could be undone, this one whether anything was done. The handler
    /// answers where it can see the action; where the AUTHOR supplies it — a python script, a
    /// playbook — only the author can, and this is where they say so. For types whose action
    /// lives on the NODE (ssh commands, an mcp tool) the node's `config_overrides.changes`
    /// says it instead, and wins over this.
    ///
    /// Nullable and never backfilled: existing rows were written when this product had no
    /// change signal at all, and inventing a value for them would record a measurement
    /// nobody took.
    /// </remarks>
    public bool? ChangesState { get; set; }

    // Opt-in escape hatch for python_snippet ONLY: when true, the script runs
    // in a RELAXED sandbox with host network access + DNS, and the import
    // allowlist is extended with netmiko/paramiko/socket/time so the author can
    // drive interactive SSH sessions the fixed `ssh` step can't (e.g. a password
    // change that prompts for confirmation). This removes the sandbox's network
    // isolation for that one snippet, so the snippet runs with far fewer
    // guardrails — SnippetService gates setting it to admin-only. Default false
    // keeps every other snippet fully isolated.
    public bool NetworkEnabled { get; set; }

    // Mermaid source describing the internal logic of this snippet/task.
    // Rendered in the UI on snippet edit AND when the user selects a node
    // in the workflow canvas — the goal is that someone opening a workflow
    // six months later understands what each task does by reading the
    // diagram instead of the Python body.
    //
    // Required for `python_snippet` and `transform` (enforced by
    // SnippetService) because those carry user-authored logic that is
    // otherwise opaque. Optional for built-in handler types (ping,
    // rest_call, ssh, integration_action, report) whose behavior is
    // self-descriptive from the type itself.
    public string? LogicDiagramMermaid { get; set; }
}
