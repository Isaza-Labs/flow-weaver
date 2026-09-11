# Skill: discovery — call the right API with the fewest round-trips

Every `discover_operations` → `operation_detail` → `execute_operation`
triplet is **three** LLM round-trips per API you need to use. A workflow
that spans NetBox + email + Infoblox can easily hit 9-12 round-trips
just during discovery, and the 60s chat deadline stops being roomy.
These rules keep the turn fast and fit the one-plan contract.

## Rule 1 — Narrow the search first, then inline details

When you already know which API you want and can name the verb, call
`discover_operations` with BOTH `api` AND `keyword`, plus
`include_details: true`. When the match is ≤5 results the handler
expands parameter + request body schemas inline and you skip
`operation_detail` entirely.

```
discover_operations({
  api: "netbox",
  keyword: "devices",
  method: "GET",
  include_details: true
})
```

If the result exceeds 5, the response tells you exactly that
(`details_skipped_reason: "result set (N) exceeds inline cap (5); narrow
the search and retry"`). **Narrow the query.** Do NOT fall back to a
separate `operation_detail` call for each — that's the pattern we're
trying to kill.

## Rule 2 — Don't re-discover what's already in context

The LLM sees every previous tool result in the same turn's message
history. If iteration 2 already returned `fw_integrations:list_palette_actions`,
iteration 4 should not call `discover_operations` again to find it —
just use the id you already have.

Re-discovery is the #1 cause of blown deadlines. If you notice you're
about to search for the same thing twice, stop: the answer is already
above you in the conversation.

## Rule 3 — Cluster calls by API, not by concept

When the user's workflow spans several integrations, resolve all ops
for ONE integration before moving to the next. That lets you reuse
the same `api` filter and keeps the LLM's attention pinned to one
schema at a time.

Bad:

```
discover_operations(keyword="list")       // returns 25 ops across APIs
discover_operations(keyword="send")       // 25 more, overlapping
discover_operations(keyword="update")     // 25 more
```

Good:

```
discover_operations(api="netbox", keyword="devices",        include_details=true)
discover_operations(api="netbox", keyword="interfaces",     include_details=true)
discover_operations(api="email",  keyword="send",           include_details=true)
```

Each narrow+typed call returns 1-3 ops with inline schemas. Three tool
calls, zero `operation_detail` needed.

## Rule 4 — `fw_*` APIs are stable: remember their common ops

The internal Flow Weaver APIs change rarely. Common operations you'll
reach for:

| Operation | Used for |
|---|---|
| `fw_workflows:create_workflow` | Draft workflow creation. **One call, no plan flow.** |
| `fw_workflows:update_workflow` | Edit nodes + edges. |
| `fw_workflows:run_workflow` | Enqueue a run with input + targets. |
| `fw_workflows:promote_workflow` | draft → qa → production. |
| `fw_snippets:list_snippets` | Find a seeded snippet by type (ping, ssh, etc.). |
| `fw_inventory:list_devices` / `query_devices` tool | List/filter devices. |
| `fw_integrations:list_integrations` | See which external integrations exist. |
| `fw_integrations:list_palette_actions` | GUIDs for `integration_action` nodes. |

You don't need to discover these. Call them directly with
`execute_operation(operation_id="fw_workflows:create_workflow", body={...})`
inside a single Plan block.

## Rule 5 — Build the batch, then execute

Once you've resolved every id you need, present ONE Plan block listing
every `execute_operation` call you'll make and ask for a single "yes".
Do NOT confirm between calls once the user approved the batch — see
`base.md` § Mutation protocol.

## When discovery IS worth the round-trips

- The user named an API you've never seen (non-baseline integration).
- You need the request body schema for a rarely-used op (e.g., bulk
  imports).
- You need to enumerate every op of a given api for a "what can X do?"
  user question.

In those cases, pay the round-trip — that's what it's for. The rules
above are about the 80 % case, not a universal ban.
