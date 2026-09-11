using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Lets the agent resolve a credential by name + type before passing
// the resulting UUID to git_* / ssh / integration tools. Always
// returns ONLY metadata — never the password, never the private key,
// never the passphrase. The platform's plaintext-stays-server-side
// invariant is preserved here as well.
//
// This handler was previously declared in the PermissionClassifier
// matrix and the default agent's whitelist, but the implementation
// was missing — calls would fail with "tool not found" until a
// operator gave the agent a literal UUID. This restores the natural
// "use the github credential" flow.
public sealed class ListCredentialsHandler : IToolHandler
{
    public string Name => "list_credentials";

    public string Description =>
        "List the stored credentials so the agent can resolve a " +
        "user-supplied name (e.g. 'github credential', 'core-ssh') to its " +
        "UUID. NEVER returns secret material — only id, name, type, " +
        "username, auth_method, has_private_key. Use BEFORE git_* / " +
        "create_remote_repository / any tool that needs an " +
        "auth_credential_id when the user mentions a credential by name. " +
        "Filter with `type` (e.g. 'git_token', 'ssh') or `name_contains` " +
        "(case-insensitive substring match).";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "type": {
              "type": "string",
              "description": "Filter by Credential.Type. Common values: ssh, netconf, snmp_v3, git_token, api_key, http_basic. Omit to list all."
            },
            "name_contains": {
              "type": "string",
              "description": "Case-insensitive substring filter on the name field. Use this to disambiguate when the user gave a partial name."
            },
            "auth_method": {
              "type": "string",
              "enum": ["password", "key"],
              "description": "Filter by auth method. Useful when picking between SSH password vs key creds."
            },
            "limit": {
              "type": "integer",
              "minimum": 1,
              "maximum": 200,
              "description": "Max rows to return. Defaults to 100."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly ICredentialRepository _credentials;
    private readonly ICurrentUser _caller;
    private readonly ILogger<ListCredentialsHandler> _logger;

    public ListCredentialsHandler(
        ICredentialRepository credentials,
        ICurrentUser caller,
        ILogger<ListCredentialsHandler> logger)
    {
        _credentials = credentials;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var typeFilter = GetString(args, "type")?.Trim();
        var nameContains = GetString(args, "name_contains")?.Trim();
        var authMethod = GetString(args, "auth_method")?.Trim().ToLowerInvariant();
        var limit = 100;
        if (args.TryGetProperty("limit", out var l)
            && l.ValueKind == JsonValueKind.Number
            && l.TryGetInt32(out var li))
            limit = Math.Clamp(li, 1, 200);

        var found = await _credentials.ListMetadataAsync(typeFilter, authMethod, nameContains, limit, ct);

        var rows = found
            .Select(c => new
            {
                credential_id = c.CredentialId,
                name = c.Name,
                type = c.Type,
                username = c.Username,
                auth_method = c.AuthMethod,
                has_private_key = c.HasPrivateKey,
                created_at = c.CreatedAt,
                updated_at = c.UpdatedAt,
            })
            .ToList();

        _logger.LogInformation(
            "ai.tool.list_credentials.ok type={Type} name_contains={NameContains} returned={Returned}",
            typeFilter ?? "*", nameContains ?? "*", rows.Count);

        return JsonSerializer.SerializeToElement(new
        {
            total = rows.Count,
            credentials = rows,
        });
    }

    private static string? GetString(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
