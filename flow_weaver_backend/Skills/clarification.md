# Skill: intake — ask before planning

Before calling `create_workflow_plan` or `create_workflow`, the agent
**must** call `evaluate_prompt_sufficiency` with the user's
description. This tool deterministically checks whether the request
has enough context (targets, required keys, name collisions,
production scope) and returns a concrete list of ambiguities.

Skipping it leads to workflows built on assumed scope: for example, an
audit workflow that returns an empty result because nobody asked which
devices were meant to be audited.

## When to call the tool

**Always, before the plan**, except in these shortcut cases:

- The conversation already answered prior `evaluate_prompt_sufficiency`
  questions and you're now going straight to the plan.
- The user explicitly asked "show me the plan without asking anything
  else".
- The request is purely read-only (`list_workflows`, `get_workflow`,
  etc.) — there's nothing to plan.

## How to invoke it

```
evaluate_prompt_sufficiency({
  "description": "<user's literal text>",
  "candidate_name": "audit-dns-prod",        // optional
  "proposed_snippet_ids": ["<uuid>", ...],    // optional
  "proposed_target_devices": ["<uuid|name>"], // optional
  "proposed_target_pools": ["<uuid|name>"]    // optional
})
```

If you already searched for reusable snippets with `list_snippets`,
pass their UUIDs in `proposed_snippet_ids` — the tool reads their
`input_schema` and detects `required` keys missing from the description.

## Response and how to use it

```json
{
  "sufficient": false,
  "missing": [
    {
      "field": "targets",
      "reason": "Snippets per_device (ping) need at least one device or pool.",
      "suggested_question": "Which devices or pools should this run against?"
    }
  ],
  "ambiguities": [
    {
      "topic": "production_scope",
      "options": ["draft", "qa", "production"],
      "suggested_question": "You mentioned production. Should we target production directly, or land in draft/qa first?"
    }
  ]
}
```

If `sufficient: false`:

1. Emit a short message with **only the suggested questions** (one per
   ambiguity or missing field). Don't move to the plan yet.
2. Number the questions (1, 2, 3) so the user can answer by index.
3. If a question depends on data you could fetch (e.g. "which pool?"),
   mention you can list them — without firing the listview on your
   own initiative yet.
4. Wait for the user's reply. When they answer, re-run
   `evaluate_prompt_sufficiency` with the enriched description (or
   the populated `proposed_*` fields). Repeat until `sufficient: true`.

If `sufficient: true`, proceed with the correct path **in a single
plan with a single confirmation** (the ONE-PLAN rule from `base.md`):

- Default route (draft): `fw_workflows:create_workflow` directly.
  **Do NOT** use `create_workflow_plan` for draft — the backend
  rejects it.
- Governance route (qa / production): `create_workflow_plan` only when
  the user explicitly asked for review or the destination is
  production.

Don't comment on the intake — the user doesn't need to know you went
through this step.

## Question patterns

The `suggested_question` strings returned by `evaluate_prompt_sufficiency`
are reference English. **Translate them into the user's language**
(see `base.md` § Language) and paraphrase to sound natural — never quote
verbatim. Keep the same intent, not the same wording. Avoid:

- Open-ended questions ("anything else I should know?") — they don't
  push the user to give concrete data.
- More than 3 questions per turn — if there are more, prioritize
  targets first, then required keys, then scope ambiguities.
- Asking for things the description already covered. If the user said
  "in the core-routers pool", don't ask for the target again.

## Example

**User:** "Create a workflow that pings every device and sends a
summary to the team."

**Agent (internal):**

```
list_snippets({ type: "ping" })
→ [{ snippet_id: "<ping-uuid>", target_mode: "per_device", ... }]

list_snippets({ type: "integration_action" })
→ [{ snippet_id: "<ia-uuid>", ... }]

evaluate_prompt_sufficiency({
  description: "...",
  proposed_snippet_ids: ["<ping-uuid>", "<ia-uuid>"]
})
→ {
  sufficient: false,
  missing: [
    { field: "targets", suggested_question: "Which devices...?" }
  ],
  ambiguities: []
}
```

**Agent (to the user):**

> Before I assemble it, two questions:
>
> 1. Should I ping every active device, or do you want
>    a specific pool (e.g. `core-routers`, `edge-switches`)?
> 2. Which channel / email should the summary go to?

**User:** "core-routers pool, send it to ops@example.com"

**Agent (internal):** re-runs `evaluate_prompt_sufficiency` with
`proposed_target_pools: ["core-routers"]` and the updated description.
Now `sufficient: true` → continues with ONE plan that calls
`fw_workflows:create_workflow` (draft; `create_workflow_plan` is only for
qa / production).

## Anti-pattern

Don't assemble the plan with invented values ("I'll assume it's
production") to save yourself a question. The user prefers a
three-line message asking for confirmation over a workflow built on a
wrong assumption. Intake is the only cheap moment to course-correct.
