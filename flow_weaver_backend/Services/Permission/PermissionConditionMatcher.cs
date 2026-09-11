using System.Text.Json;

namespace flow_weaver_backend.Services.Permission;

// Matches a PermissionGrant.Conditions object against a call context. Same
// philosophy as PolicyEvaluator.Matches — AND-joined dimensions, "any-in-list"
// for arrays, an absent/empty condition object means "any context" — but with
// ALLOW semantics: the grant matches only when EVERY condition it declares is
// satisfied, and a condition the context cannot answer (e.g. an env-scoped
// grant with no env in context) does NOT match. That keeps a scoped grant from
// ever applying to an unscoped call.
//
// Conditions shape (all keys optional):
//   { "environment": [...], "device_role": [...], "device_pool": [...],
//     "device_ids": ["<uuid>", ...], "resource": { "type": "...", "id": "<uuid>" },
//     "mcp_server": ["<uuid>", ...], "mcp_tool": ["<tool-name>", ...] }
internal static class PermissionConditionMatcher
{
    public static bool Matches(JsonElement conditions, PermissionContext ctx)
    {
        // null / undefined / non-object => unconditional grant.
        if (conditions.ValueKind != JsonValueKind.Object) return true;

        if (conditions.TryGetProperty("environment", out var env)
            && !AnyEquals(env, ctx.Environment))
            return false;

        if (conditions.TryGetProperty("device_role", out var roles)
            && !AnyInList(roles, ctx.DeviceRoles))
            return false;

        if (conditions.TryGetProperty("device_pool", out var pools)
            && !AnyInList(pools, ctx.DevicePoolNames))
            return false;

        if (conditions.TryGetProperty("device_ids", out var ids)
            && !AnyGuidInList(ids, ctx.DeviceIds))
            return false;

        if (conditions.TryGetProperty("resource", out var resource)
            && !ResourceMatches(resource, ctx))
            return false;

        if (conditions.TryGetProperty("mcp_server", out var srv)
            && !AnyGuidEquals(srv, ctx.McpServerId))
            return false;

        // MCP tool names are case-SENSITIVE (a server may expose both `read_file`
        // and `READ_FILE` as distinct tools), so this must not use the
        // case-insensitive AnyEquals that `environment` relies on.
        if (conditions.TryGetProperty("mcp_tool", out var tool)
            && !AnyEqualsOrdinal(tool, ctx.McpToolName))
            return false;

        return true;
    }

    // Case-sensitive variant of AnyEquals, for identifiers that are not
    // case-folded (MCP tool names).
    private static bool AnyEqualsOrdinal(JsonElement list, string? value)
    {
        if (list.ValueKind != JsonValueKind.Array || string.IsNullOrEmpty(value)) return false;
        foreach (var el in list.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String
                && string.Equals(el.GetString(), value, StringComparison.Ordinal))
                return true;
        return false;
    }

    // Context value must appear in the (non-empty, string) array. A malformed
    // or empty array, or a missing context value, does not match — a declared
    // condition is a constraint, so failing to satisfy it denies.
    private static bool AnyEquals(JsonElement list, string? value)
    {
        if (list.ValueKind != JsonValueKind.Array || string.IsNullOrEmpty(value)) return false;
        foreach (var el in list.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String
                && string.Equals(el.GetString(), value, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool AnyInList(JsonElement list, IReadOnlyList<string>? ctxValues)
    {
        if (list.ValueKind != JsonValueKind.Array || ctxValues is null || ctxValues.Count == 0)
            return false;
        var needles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in list.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                needles.Add(s);
        foreach (var v in ctxValues)
            if (needles.Contains(v)) return true;
        return false;
    }

    private static bool AnyGuidInList(JsonElement list, IReadOnlyList<Guid>? ctxIds)
    {
        if (list.ValueKind != JsonValueKind.Array || ctxIds is null || ctxIds.Count == 0)
            return false;
        var needles = new HashSet<Guid>();
        foreach (var el in list.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && Guid.TryParse(el.GetString(), out var g))
                needles.Add(g);
        foreach (var id in ctxIds)
            if (needles.Contains(id)) return true;
        return false;
    }

    // Single-Guid analog of AnyEquals: the context id must appear in the array.
    private static bool AnyGuidEquals(JsonElement list, Guid? value)
    {
        if (list.ValueKind != JsonValueKind.Array || value is null) return false;
        foreach (var el in list.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String
                && Guid.TryParse(el.GetString(), out var g) && g == value.Value)
                return true;
        return false;
    }

    private static bool ResourceMatches(JsonElement resource, PermissionContext ctx)
    {
        if (resource.ValueKind != JsonValueKind.Object) return false;
        if (ctx.ResourceType is null || ctx.ResourceId is null) return false;

        var type = resource.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;
        if (!string.Equals(type, ctx.ResourceType, StringComparison.OrdinalIgnoreCase)) return false;

        var id = resource.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
            ? i.GetString() : null;
        return Guid.TryParse(id, out var rid) && rid == ctx.ResourceId.Value;
    }
}
