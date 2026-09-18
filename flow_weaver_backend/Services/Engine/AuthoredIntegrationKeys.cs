using System.Text.Json;

namespace flow_weaver_backend.Services.Engine;

// PythonHandler hands a script the base URL and auth headers of every
// integration named by a `<handle>_integration_id` key in the object it reads:
// `input` when the payload has one, the payload itself otherwise — never both. The
// script can print those headers, so the integration must be one the workflow's
// author chose — not one picked by whoever starts the run (run input, trigger
// input_defaults) or by data flowing through it (a template resolving to a step
// output or a webhook field).
//
// A key is authored when the node's config_overrides holds the same key, at the
// same place, as a literal string (no template) with the same value. A subflow
// child additionally trusts its own run input: that input was built from the
// parent's subflow node and already filtered with this rule (Strip).
public static class AuthoredIntegrationKeys
{
    private const string Suffix = "_integration_id";
    private const string InputKey = "input";

    // Dotted paths of integration keys in `payload` that nobody authored.
    public static IReadOnlyList<string> Unauthored(
        JsonElement payload, JsonElement config, JsonElement? trustedInput = null)
    {
        var result = new List<string>();
        foreach (var (path, _, _, _) in Offending(payload, config, trustedInput))
            result.Add(path);
        return result;
    }

    // Copy of `payload` without the unauthored integration keys.
    public static JsonElement Strip(
        JsonElement payload, JsonElement config, JsonElement? trustedInput = null)
    {
        var offending = Offending(payload, config, trustedInput).ToList();
        if (offending.Count == 0) return payload;

        // By (key, value): MergePayloads can leave the same key twice (run input,
        // then config), and only the unauthored copy must go.
        var topLevel = offending.Where(o => !o.inInput).Select(o => (o.key, o.value)).ToHashSet();
        var nested = offending.Where(o => o.inInput).Select(o => (o.key, o.value)).ToHashSet();

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var prop in payload.EnumerateObject())
            {
                if (IsListed(topLevel, prop)) continue;
                w.WritePropertyName(prop.Name);
                if (prop.NameEquals(InputKey) && prop.Value.ValueKind == JsonValueKind.Object && nested.Count > 0)
                {
                    w.WriteStartObject();
                    foreach (var inner in prop.Value.EnumerateObject())
                    {
                        if (IsListed(nested, inner)) continue;
                        w.WritePropertyName(inner.Name);
                        inner.Value.WriteTo(w);
                    }
                    w.WriteEndObject();
                }
                else
                {
                    prop.Value.WriteTo(w);
                }
            }
            w.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private static bool IsListed(HashSet<(string key, string value)> set, JsonProperty prop)
        => prop.Value.ValueKind == JsonValueKind.String
           && set.Contains((prop.Name, prop.Value.GetString() ?? string.Empty));

    private static IEnumerable<(string path, string key, string value, bool inInput)> Offending(
        JsonElement payload, JsonElement config, JsonElement? trustedInput)
    {
        if (payload.ValueKind != JsonValueKind.Object) yield break;

        // Same choice PythonHandler makes: `input` if present, else the payload.
        // Scanning the other object too would fail a step over a key the handler
        // never reads, with an error the user cannot act on.
        var inInput = payload.TryGetProperty(InputKey, out var input)
                      && input.ValueKind == JsonValueKind.Object;
        var scanned = inInput ? input : payload;

        foreach (var (key, value) in IntegrationKeys(scanned))
            if (!Authored(key, value, Scope(config, inInput), Scope(trustedInput, inInput)))
                yield return (inInput ? $"{InputKey}.{key}" : key, key, value, inInput);
    }

    // Only the entries PythonHandler would act on: string, non-blank values.
    private static IEnumerable<(string key, string value)> IntegrationKeys(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (!prop.Name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) continue;
            // `_integration_id` on its own leaves an empty handle, which the
            // handler skips — so it grants nothing and is not worth failing over.
            if (prop.Name.Length == Suffix.Length) continue;
            if (prop.Value.ValueKind != JsonValueKind.String) continue;
            var value = prop.Value.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            yield return (prop.Name, value);
        }
    }

    private static JsonElement? Scope(JsonElement? source, bool inInput)
    {
        if (source is not { ValueKind: JsonValueKind.Object } obj) return null;
        if (!inInput) return obj;
        return obj.TryGetProperty(InputKey, out var input) && input.ValueKind == JsonValueKind.Object
            ? input
            : null;
    }

    private static bool Authored(string key, string value, JsonElement? config, JsonElement? trusted)
    {
        if (config is { } c
            && c.TryGetProperty(key, out var authored)
            && authored.ValueKind == JsonValueKind.String
            && authored.GetString() is { } literal
            && !literal.Contains("{{", StringComparison.Ordinal)
            && string.Equals(literal, value, StringComparison.Ordinal))
            return true;

        return trusted is { } t
            && t.TryGetProperty(key, out var inherited)
            && inherited.ValueKind == JsonValueKind.String
            && string.Equals(inherited.GetString(), value, StringComparison.Ordinal);
    }
}
