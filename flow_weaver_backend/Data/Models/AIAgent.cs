using System.Text.Json;

namespace flow_weaver_backend.Models;

public class AIAgent : BaseModel
{
    public Guid AIAgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? ProviderId { get; set; }
    public string? ModelOverride { get; set; }
    public string SystemPrompt { get; set; } = string.Empty;
    public List<string> Tools { get; set; } = new();
    public int MaxIterations { get; set; }
    public double Temperature { get; set; }
    public JsonElement Config { get; set; } = default;
    public bool Enabled { get; set; }
}
