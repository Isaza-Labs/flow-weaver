using System.Text.Json;

namespace flow_weaver_backend.Models;

public class Job : BaseModel
{
    public Guid JobId { get; set; }
    public string Type { get; set; } = string.Empty;
    public JsonElement Payload { get; set; } = default;
    public string Tag { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ClaimedBy { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Deadline for the current claim. A reclaim sweeper flips expired
    // claims back to 'pending' so a crashed worker does not strand the job.
    public DateTime? LeaseExpiresAt { get; set; }
}
