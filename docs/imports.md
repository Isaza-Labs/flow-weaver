# Workflow import

The import wizard at `/workflows/import` accepts workflow definitions
from FlowWeaver v1 (our own export), n8n, Itential IAP / Operations
Manager, and any generic JSON/YAML DAG. The pipeline detects the
format, translates to our v1 schema, surfaces missing dependencies and
conflicts, and lets the user resolve each before commit.

## Architecture

```
[ User upload ]
      │
      ▼
POST /api/workflow/import/analyze
      │   (returns token; pipeline runs async)
      ▼
[ ImportDraftCache (in-memory, 30-min TTL) ]
      │
      ▼ ──► IDslDetector[]  (FlowWeaverV1 / N8n / Itential / GenericDag)
      │
      ▼ ──► IDslTranslator   (matched by FormatName; AgentTranslator fallback)
      │
      ▼ ──► DependencyResolver  (missing snippets / integrations / vendor cmds)
      │
      ▼ ──► ConflictDetector    (name collision + structural duplicate)
      │
      ▼ ──► WorkflowRollbackAnalyzer (S13.6 — flag non-reversible nodes)
      │
      ▼
[ AnalysisReport stored on draft ]
      │
SSE  │ /api/workflow/import/{token}/stream
      │
      ▼
[ User reviews + resolves ]
      │
      ▼
POST /api/workflow/import/{token}/commit
      │
      ▼
[ Stubs created → integrations created (needs_config) → workflow persisted ]
```

## Supported formats

| Format | Detector confidence | Translator | Round-trip exact? |
|---|---|---|---|
| **FlowWeaver v1** | 1.0 when `schema_version=v1` is present | `FlowWeaverV1Translator` (identity) | yes |
| **n8n** | 0.95 when `n8n-nodes-*` types present | `N8nTranslator` (deterministic mapping) | no — n8n features without a v1 equivalent are dropped with a warning |
| **Itential IAP / Operations Manager** | 0.95 when tasks have `app` keys | `ItentialTranslator` (deterministic mapping) | no — same caveat |
| **Generic DAG** | 0.3 baseline | `AgentTranslator` (LLM-driven) | no — agent best-effort |
| **Unknown** | 0.0 | `AgentTranslator` | no |

Adding a new format:

1. Implement `IDslDetector` with a stable `FormatName` and a `Detect()`
   that scores 0..1.
2. Implement `IDslTranslator` returning a `TranslationResult` with v1
   JSON + notes + warnings.
3. Register both with the same `FormatName` in `Program.cs` DI.

The agent translator is the catch-all; it handles whatever the
specific detectors don't claim.

## Endpoints

### `POST /api/workflow/import/analyze`
Body: raw YAML / JSON (up to 5 MiB). Optional `?format_hint=<name>` to
force a specific detector.
Response: `{ "import_token": "<guid>", "status": "pending" }` (HTTP 202).

The pipeline runs in the background; the response returns immediately.

### `GET /api/workflow/import/{token}`
Poll endpoint. Returns the latest `ImportDraftSnapshot`:

```jsonc
{
  "import_token": "...",
  "status": "pending|analyzing|ready|failed|committed",
  "progress": "Translating from n8n...",
  "report": null | <AnalysisReport>,
  "error": null
}
```

### `GET /api/workflow/import/{token}/stream`
Server-Sent Events. One frame per status change + one per intermediate
progress message. Closes when status reaches `ready` / `failed` /
`committed`.

```
data: {"type":"snapshot","data":{"status":"analyzing","progress":"Parsing upload..."}}

data: {"type":"progress","data":{"status":"analyzing","progress":"Translating from n8n..."}}

data: {"type":"report_ready","data":{"status":"ready","report":{...}}}
```

### `POST /api/workflow/import/{token}/generate-snippet`
Body: `{ "id_in_import": "send_slack", "prompt_hint": "..." }`.
Calls the agent to draft a Snippet body for one missing reference.
Returns `{ "generated_snippet": {...}, "confidence": 0.8 }` or a
fallback stub + error message when the agent fails.

The wizard sends the returned `generated_snippet` back in the commit
body under `snippets[id_in_import].generated_snippet`.

### `POST /api/workflow/import/{token}/commit`
Body:

```jsonc
{
  "conflict_resolution": "rename" | "replace" | "keep_existing" | "fresh_copy",
  "new_name": "...",                          // when conflict_resolution = rename
  "target_environment": "draft",
  "duplicate_action": "skip" | "update_existing" | "import_as_new",
  "snippets": {
    "send_slack": {
      "action": "stub" | "generated" | "map",
      "target_id": "...",                      // when action = map
      "generated_snippet": {...}               // when action = generated
    }
  },
  "integrations": {
    "netbox_api": {
      "action": "create_needs_config" | "map",
      "target_id": "..."
    }
  }
}
```

Response: `{ "workflow_id": "...", "name": "...", "environment": "draft", "warnings": [...] }`.

Per-resource RBAC: when `conflict_resolution` is `replace`, the caller
must hold at least `editor` per-resource on the target workflow.

### `DELETE /api/workflow/import/{token}`
Explicit cache cleanup. Idempotent.

## What the wizard does for each missing dependency

### Snippets

Three choices per missing snippet:

| Action | Behaviour |
|---|---|
| `stub` (default) | `SnippetStubBuilder` creates an empty Snippet of the inferred type. User edits the body later in `/snippets/{id}`. |
| `generated` | The wizard calls `/generate-snippet`; the agent drafts a body the user reviews before commit. |
| `map` | Wire the imported node to an existing Snippet by id. |

### Integrations

Two choices:

| Action | Behaviour |
|---|---|
| `create_needs_config` (default) | New `Integration` row with `Status="needs_config"`, empty `AuthConfig`, and the inferred `BaseURL`. **Runs that reference it refuse to start until an admin completes the credentials.** |
| `map` | Wire the imported references to an existing Integration by id. |

### Vendor commands

Reported as warnings only; the validator catches unknown `device_type`
references post-import. Operator action: seed
`Skills/vendors/<device_type>.yaml` or extend `vendor_commands` via the
admin UI.

## Conflict resolution

### Name collision

When a workflow with the same `name` already exists in the target
environment, the wizard exposes four options:

| Option | Outcome |
|---|---|
| `rename` | The import lands with the user-typed `new_name`. |
| `fresh_copy` | The import gets a timestamped suffix (`name (imported 2026-05-08 14:30)`). |
| `replace` | Soft-deletes the existing row and inserts the imported one (requires per-resource `editor`). |
| `keep_existing` | Cancels the import entirely. |

### Structural duplicate

`DuplicateFingerprint` computes a SHA-256 over the normalised graph
(snippet types + config + edges, sorted; ids and positions ignored).
When the fingerprint matches an existing workflow ≥ 95 %:

| Option | Outcome |
|---|---|
| `import_as_new` (default) | Imports anyway with a fresh GUID + (possibly suffixed) name. |
| `skip` | Cancels the import. |
| `update_existing` | Currently behaves like `replace`. |

## Rollback risk during import

The wizard runs `WorkflowRollbackAnalyzer` (S13.6) on the proposed
workflow and surfaces the report:

- Red banner when the import contains non-reversible nodes (`ssh`,
  `ansible_playbook`).
- Yellow banner when nodes are `requires_compensation` without a
  failure edge.

This is informational — the import does not block. The user can still
commit; the rollback gate fires later if/when they attempt rollback
on the imported workflow.

## Audit events

Every step writes to `/admin/audit` (chip "Workflow imports",
`action_prefix=workflow_import`):

| Action | When |
|---|---|
| `workflow_import.started` | Upload accepted, token issued |
| `workflow_import.committed` | Workflow + dependencies persisted |
| `workflow_import.failed` | Pipeline failure (parse / translate / commit) |
| `integration.created_needs_config` | Auto-created integration during commit |

## Operational notes

- **Per-instance cache**: `ImportDraftCache` lives in process memory.
  Multi-instance deployments need sticky session affinity or a swap to
  a distributed cache.
- **TTL**: 30 minutes from creation. Inactive drafts are swept on the
  next access. Explicit `DELETE` clears immediately.
- **File size cap**: 5 MiB by request body limit.
- **Rate limits**:
  - `/analyze` and `/commit` → `WriteNormal` (60/min/user).
  - `/generate-snippet` → `AiChat` (30/hour/user).
  - `/stream` is unrate-limited; one stream per token.

## Known gaps

- The agent translator's quality varies by model. Strongly typed
  formats (v1, n8n, Itential) hit specific translators first.

## Closed follow-ups (FU-1 — FU-5)

All entries below were S15 follow-ups; they shipped together in the
same release cycle.

- **FU-1 transaction wrap (commit)**: the commit endpoint now opens an
  EF transaction covering both `SaveChangesAsync` calls. Schema-,
  reference-, and final-save failures roll back, so a partial commit
  never leaves orphan stubs or integrations behind. EF InMemory (used
  in tests) doesn't support transactions; we detect the provider and
  skip the wrap there.
- **FU-2 executor pre-flight integration check**: `WorkflowExecutor.
  EnqueueRunAsync` now refuses runs whose graph references any
  integration in `needs_config` status. The user sees the failure at
  enqueue time, not after the first step dispatches.
- **FU-3 `analyze_foreign_workflow` chat tool**: the import pipeline
  is now exposed as an AI tool. A user in `/ai/chat` can paste a
  foreign workflow (≤ 16 KiB) and get back format detection +
  translation summary + missing dependencies. Read-only — for the full
  commit flow, the wizard at `/workflows/import` is still the surface.
- **FU-4 `AgentResponseParser` unit tests**: parsing logic for the
  agent translator's response (markdown-fence stripping, wrapped vs
  bare JSON shape, non-JSON fallback) is now a public static class
  covered by 12 tests. The LLM call itself is still untested; that
  remains a manual smoke check.
- **FU-5 `update_existing` deep merge**: `duplicate_action=update_existing`
  now mutates the structural-duplicate row in place instead of
  soft-deleting and re-inserting. `WorkflowId`, `CreatedBy`,
  `CreatedAt`, and `Environment` are preserved; `Nodes`, `Edges`,
  `InputSchema`, `Description`, `Name` come from the import; `Version`
  bumps by 1; `Metadata` is shallow-merged with imported keys winning.
  The current state is snapshotted into a `WorkflowVersion` so the
  rollback gate still has somewhere to revert. Per-resource `editor`
  grant is enforced (same as `replace`).
