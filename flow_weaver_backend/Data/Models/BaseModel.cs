namespace flow_weaver_backend.Models;

// Base for every persisted entity. IsActive enables the soft-delete pattern
// used across CRUD services.
public abstract class BaseModel
{
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

