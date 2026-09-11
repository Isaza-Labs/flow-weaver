using System.Text.Json;

namespace flow_weaver_backend.Models;

public class DevicePool : BaseModel
{
    public Guid DevicePoolId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JsonElement FilterRules { get; set; } = default;
    public List<Guid> StaticMembers { get; set; } = new();

    // Environments this pool may be targeted from. Same semantics as the
    // Device trio, and applied on top of it: a run must be allowed by the
    // pool AND by each member, so a pool open to production still only
    // reaches the members that allow production.
    public bool AllowDraft { get; set; } = true;
    public bool AllowQa { get; set; }
    public bool AllowProduction { get; set; } = true;
}
