using System.Text.Json;

namespace flow_weaver_backend.Models;

// A granular permission grant: binds a set of subjects (users) to a set of
// catalogue capabilities, optionally scoped by ABAC conditions (environment /
// device / resource). This is the object an admin authors "like a policy" and
// assigns to users (plan_rbac_granular.md §3). A caller's effective
// permissions = the union of the grants they are a subject of, each evaluated
// against the current call context.
//
// Subjects and capabilities are native Postgres arrays (uuid[] / text[]) rather
// than child tables: the grant set is small and the resolver only ever
// loads a user's grants and evaluates them in memory, so the array shape keeps
// both the schema and the code minimal.
//
// admin is NOT represented here — an admin bypasses every permission check and
// is never a subject of any grant.
public class PermissionGrant : BaseModel
{
    public Guid PermissionGrantId { get; set; }

    // Deterministic for the two seeded bundles ("builtin.operator",
    // "builtin.viewer"); free-text for admin-authored grants.
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Disabled grants are ignored by the resolver but kept for audit/history.
    public bool Enabled { get; set; } = true;

    // True for the seeded operator/viewer bundles that reproduce the legacy
    // 3-tier behaviour. The seeder keeps their Capabilities in sync with the
    // catalogue; admins manage their own (IsBuiltIn=false) grants separately.
    public bool IsBuiltIn { get; set; }

    // User ids this grant applies to. Maps to uuid[].
    public List<Guid> SubjectIds { get; set; } = new();

    // Capability keys from CapabilityCatalog. Maps to text[].
    public List<string> Capabilities { get; set; } = new();

    // ABAC conditions, same shape as a Policy `when` object:
    //   { "environment": [...], "device_role": [...], "device_pool": [...],
    //     "device_ids": [...], "resource": { "type": "...", "id": "..." } }
    // An empty object / null means "any context" (unconditional). Maps to jsonb.
    public JsonElement Conditions { get; set; }
}
