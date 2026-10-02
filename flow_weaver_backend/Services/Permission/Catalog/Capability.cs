namespace flow_weaver_backend.Services.Permission.Catalog;

// Context dimensions a PermissionGrant can condition a capability on. Mirrors
// the PolicyEvaluator `when` vocabulary (env / device_role / device_pool /
// resource) so grants and policies speak the same language and can reuse the
// same matcher. A capability whose dimensions are `None` is a global verb
// (e.g. user.manage) — conditioning it on env/device/resource is
// meaningless, and the grant builder hides those inputs.
[Flags]
public enum ContextDimensions
{
    None = 0,
    Environment = 1 << 0,   // draft | qa | production
    Device = 1 << 1,        // device_role / device_pool / device_ids
    Resource = 1 << 2,      // resource { type, id } — a specific workflow/integration/…
    Mcp = 1 << 3,           // mcp_server (uuid) / mcp_tool (name) — a specific MCP server/tool
}

// The legacy role tier that implies a capability TODAY (the Admin/Operator/
// Viewer ASP.NET policies in Program.cs). Drives the compatibility seed
// (builtin.operator / builtin.viewer) so migrating to the granular model
// reproduces the exact pre-refactor behaviour.
//
// `admin` is intentionally absent: admin bypasses every permission check and
// never needs a grant, so no capability is "admin-tier only" from a seeding
// standpoint — Admin-gated capabilities simply belong to neither the viewer
// nor the operator built-in bundle.
public enum LegacyTier
{
    Viewer,     // any authenticated user today (class-level [Authorize(Policy="Viewer")])
    Operator,   // operator or admin today ([Authorize(Policy="Operator")])
    Admin,      // admin only today ([Authorize(Policy="Admin")]) — bypassed by admin, granted to no built-in bundle
}

// One atomic, enforceable capability. `Key` is `domain.action`, lower-case.
//
// Capabilities are CODE-DEFINED: each one maps to a real enforcement point in
// the codebase, so they are never created from the UI. A PermissionGrant
// references them by Key; the grant is the thing an admin authors and assigns
// to users.
public sealed record Capability(
    string Key,
    string Domain,
    string Description,
    ContextDimensions Conditionable,
    LegacyTier LegacyTier);
