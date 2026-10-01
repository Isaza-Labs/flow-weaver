namespace flow_weaver_backend.Services.Ai.Secrets;

// Replaces ${secret:<source>:<id>:<field>} placeholders with plaintext
// pulled from the encrypted store. Callers only pass the template; the
// resolver owns the lookups and the decryption.
//
// Supported source shapes:
//   ${secret:secret:<name>:value}                 → Secret.EncryptedValue
//   ${secret:credential:<id|name>:password}       → Credential.EncryptedPassword
//   ${secret:credential:<id|name>:private_key}    → Credential.EncryptedPrivateKey
//   ${secret:credential:<id|name>:username}       → Credential.Username (plaintext)
//   ${secret:ai_provider:<id|name>:api_key}       → AIProvider.EncryptedApiKey
//   ${secret:integration:<id|name>:<path.in.auth>} → Integration.AuthConfig dotted path
//   ${secret:session:current:jwt}                 → raw JWT access token of the
//                                                   request that invoked the agent.
//                                                   Used by specs that point at
//                                                   the backend itself so the
//                                                   executor calls run under the
//                                                   same permissions as the user.
//
// The exact name vs id resolution is attempted in that order: a valid
// Guid parses as id first; anything else falls through to name lookup.
public interface ISecretResolver
{
    // Non-templated lookup: returns the raw value for a single reference
    // or null when the reference cannot be resolved. Used by tools that
    // know up-front what they need (e.g. an admin testing a connection).
    Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct);

    // Substitutes every ${secret:...} occurrence in `template`. Unresolved
    // references are left literal so the operator sees the original marker
    // in debug output instead of a silent empty string.
    Task<string> SubstituteAsync(string template, CancellationToken ct);
}
