using System.Text.Json;

namespace flow_weaver_backend.Models;

// Corporate guardrail stored in the DB. The Rule is a JSON document
// that the PolicyEvaluator walks at write/run time; we keep it as a
// JsonElement instead of a strongly-typed DSL so the shape can evolve
// without another migration. Shape today:
//
// {
//   "action": "deny",
//   "reason": "no se pueden tocar routers core sin ventana de cambio",
//   "when": {
//     "env": ["production", "qa"],           // optional; any env if absent
//     "device_role": ["core"],                // optional; matches Device.Role
//     "device_pool": ["core-routers"],         // optional; matches DevicePool.Name
//     "snippet_type": ["ssh", "integration_action"],
//     "description_contains": ["bgp", "reload"] // optional; matches workflow.Description
//   }
// }
//
// The evaluator short-circuits on the first matching deny. Allow rules
// aren't supported yet — default-allow, opt-in-deny is easier to reason
// about and matches how firewalls tend to fail.
public class Policy : BaseModel
{
    public Guid PolicyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JsonElement Rule { get; set; } = default;
    public bool Enabled { get; set; } = true;
}
