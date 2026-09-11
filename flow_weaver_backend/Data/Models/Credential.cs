using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Models;

public class Credential : BaseModel
{
    public const string AuthMethodPassword = "password";
    public const string AuthMethodKey = "key";

    public Guid CredentialId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Username { get; set; }

    // Which auth method the SSH handler (and anything else that consumes
    // credentials) should prefer. Defaults to "password" so rows created
    // before key-auth support keep working. A value of "key" requires
    // EncryptedPrivateKey to be populated — the handler fails fast with
    // a clear message if it's missing.
    public string AuthMethod { get; set; } = AuthMethodPassword;

    [JsonIgnore]
    public byte[]? EncryptedPassword { get; set; }

    [JsonIgnore]
    public byte[]? EncryptedPrivateKey { get; set; }

    // Optional passphrase protecting EncryptedPrivateKey. Stored encrypted
    // with the same DataProtection purpose as the other secrets; null
    // when the key is unencrypted at rest (which is the case for most
    // admins that want the UX of "paste a PEM, hit save").
    [JsonIgnore]
    public byte[]? EncryptedKeyPassphrase { get; set; }

    public JsonElement Extra { get; set; } = default;
}
