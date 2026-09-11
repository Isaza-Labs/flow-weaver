# Permissions & RBAC

FlowWeaver is moving from a coarse 3-tier model (Admin / Operator / Viewer) to
a **granular, capability-based** model (ABAC-lite) where users can only perform
the actions explicitly granted to them, optionally conditioned by **environment**
and **device**. Grants are authored like policies and assigned to users. The
migration is behind an application-wide rollout switch, so nothing changes until
an admin opts the deployment in.

See `plan_rbac_granular.md` for the full design and phase breakdown.

## Core concepts

| Concept | What it is | Where |
|---|---|---|
| **Capability** | An atomic, code-defined verb, `domain.action` (e.g. `workflow.run`, `secret.read`). ~80 of them. Not user-editable — each maps to a real enforcement point. | `Services/Permission/Catalog/CapabilityCatalog.cs` |
| **Permission grant** | A named object binding **subjects** (users) to **capabilities** under optional **conditions** (env / device / resource). Authored like a policy; assigned to users. | `permission_grants` table, `PermissionGrantService` |
| **Effective permissions** | The resolver that answers "does this caller hold capability X (in this context)". | `IEffectivePermissions` |

Two invariants:

- **`admin` bypasses every permission check.** It is a superuser, not a bundle,
  so adding a new capability never locks admin out.
- **Default-deny.** A user can do only what their grants allow (reads included).
  After migration, every existing user is backfilled into a built-in grant that
  reproduces their old role, so behaviour is preserved.

## Grant shape

```jsonc
{
  "name": "QA operators",
  "enabled": true,
  "subject_ids": ["<user-uuid>", ...],          // the assignment
  "capabilities": ["workflow.run", "device.exec.read"],
  "conditions": {                                // ABAC — empty object = "any context"
    "environment": ["qa"],
    "device_role": ["access"],
    "device_pool": ["access-switches"],
    "device_ids": ["<device-uuid>"],
    "resource": { "type": "workflow", "id": "<workflow-uuid>" }
  }
}
```

Condition semantics mirror the policy evaluator: every declared dimension is
AND-joined, arrays are "any-in-list", an absent/empty object means unconditional.
**Allow-only**: a condition the call context can't answer does not match, so a
scoped grant never leaks into an unscoped call.

## Built-in grants

Two read-only grants are seeded on first boot — `builtin.operator` and
`builtin.viewer` — whose capability sets equal the legacy tiers. Every non-admin
user is a subject of the one matching their `User.Role`, kept in sync on every
role change (dual-write). Admins are subjects of neither (they bypass). These
grants can't be edited via the API; the reserved `builtin.` name prefix is
blocked for custom grants.

## Enforcement across transports

The same caller identity and the same resolver back every transport, so a
channel or the agent can never exceed the user's real permissions.

- **Web / API** — `[HasPermission("workflow.run")]` on controllers, resolved by
  `PermissionAuthorizationHandler` (admin-bypass → granular grants → legacy tier).
  Env/device/resource conditions are enforced deeper, at the enforcement point
  (e.g. `PromotionService` gates `workflow.promote` on the target environment;
  `WorkflowController.Run` gates `workflow.run` on the workflow's environment +
  resource before enqueuing).
- **AI agent** — `ToolDispatcher` maps each tool to a capability
  (`ToolCapabilityMap`) and checks the caller's grants; unmapped tools are
  default-denied in granular mode. The autonomy tier (human_only) and mutation
  budget are unchanged. `execute_operation` re-enters HTTP, so the endpoint's
  `[HasPermission]` re-checks under the caller's identity.
- **Messaging (Slack/Teams/Telegram/…)** — the channel's `MaxRole` becomes a
  **capability ceiling** that intersects the linked user's grants (narrow only).
  It caps even admins ("no escalation by transport") and rides the agent's
  self-call via a `cap_ceiling` JWT claim.

## Management API (admin)

`/api/permission-grants` (all `[Authorize(Policy="Admin")]` for now):

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/permission-grants` | list grants |
| POST | `/api/permission-grants` | create a grant |
| PUT | `/api/permission-grants/{id}` | edit (rejects built-ins) |
| DELETE | `/api/permission-grants/{id}` | soft-delete (rejects built-ins) |
| POST/DELETE | `/api/permission-grants/{id}/subjects/{userId}` | assign / unassign a user |
| GET | `/api/permission-grants/capabilities` | the capability catalogue (for the builder UI) |

Capabilities in a grant are validated against the catalogue; unknown keys are
rejected.

## Rollout switch — `rbac_mode`

Enforcement mode is **application-wide**: `legacy` (default) or `granular`. It
lives on the singleton `app_settings` row and is read through
`IAppSettingsService`.

```bash
PUT /api/admin/settings
{ "rbac_mode": "granular" }
```

- **`legacy`** — `[HasPermission]` reproduces the capability's original tier and
  the agent keeps the old role matrix. Identical to the pre-refactor behaviour.
- **`granular`** — the coarse HTTP gate and the agent gate consult the caller's
  grants; contextual (env/device/resource) checks fire in the service layer.

Cached for 60 s; the change is audited under `app_settings.rbac_mode.changed`
and edited from `/admin/settings`.
Flip back to `legacy` at any time with no restart. Because the backfill gives
every user grant-parity with their role, flipping to `granular` is behaviour-
neutral until you start authoring custom grants.

## Legacy per-resource overlay (still present)

The earlier per-resource overlay (`ResourcePermission`, roles
`owner > editor > runner > viewer` on a single workflow/integration, gated by
`permissions_granular_gating_enabled`) is unchanged and still ships disabled.
The granular model above is the go-forward replacement; folding resource-scoped
grants into `permission_grants` (via `conditions.resource`) is a tracked
follow-up.

## Follow-ups (not yet wired)

- Per-command device enforcement in `SshHandler` (`device.exec.*` against the
  concrete device role/pool/env at execution time). This also covers the
  **device** dimension of `workflow.run` — its env/resource conditions are now
  enforced at enqueue (`WorkflowController.Run`), but the request's target
  devices are only checked once a step actually touches them.
- Data migration of `resource_permissions` → `permission_grants`.
- Removing the legacy `Admin/Operator/Viewer` ASP.NET policies once the
  deployment runs on `granular` (admin-only controllers still use
  `[Authorize(Policy="Admin")]`).
