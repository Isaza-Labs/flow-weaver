namespace flow_weaver_backend.Models;

// Named secret used by the agent's ExecuteOperation tool
// and the REST spec executor. Values are encrypted at rest with the same
// IDataProtection key-ring used for device credentials — the admin UI
// never returns plaintext after creation; rotation means writing a new
// Value.
//
// Secrets resolve via the pattern
//   ${secret:<source>:<id>:<field>}
// where <source> = "secret" resolves to this table by Name, and other
// sources (credential, integration, ai_provider) plug into SecretResolver
// without needing a row here. The Name column is the lookup key used in
// spec templates, so uniqueness is enforced by index.
public class Secret : BaseModel
{
    public Guid SecretId { get; set; }

    // Lookup key used by `${secret:secret:<name>:value}`. Human-readable,
    // lowercase-ish, unique.
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    // Ciphertext of the secret payload. We store raw bytes so the encryption
    // layer can pick its own envelope format.
    public byte[] EncryptedValue { get; set; } = Array.Empty<byte>();

    // Who created/last-touched the row. Useful for audit trails when
    // a workflow breaks because a rotated secret expired.
    public string? CreatedBy { get; set; }
}
