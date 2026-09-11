using System.Text.Json;
using System.Text.Json.Nodes;

namespace flow_weaver_backend.Tests.Conformance;

// The vector's `normalize` rules (kit spec §4.2), applied to BOTH expected and actual
// before the equivalence mode runs. Redaction is what lets an `exact` comparison cover
// output that carries volatile fields — an audit event's `event_id` and `timestamp`, a
// run id — without demoting the whole vector to a subset check, which is where real
// divergence hides.
//
// Two rules are implemented, and they are the two the kit's own examples use:
//
//   redact:<field>   every occurrence of <field>, at any depth, becomes the string
//                    "<redacted>". The field must still be PRESENT — redacting is not
//                    the same as ignoring, and a missing field is still a failure.
//   sort:<field>     the array at <field>, at any depth, is sorted by each element's
//                    canonical form. For collections the contract does not order
//                    (import notes, capability lists).
//
// An unknown rule is left to fail loudly rather than being ignored: a normalize rule
// nobody implements is a comparison nobody is really making.
public static class Normalizer
{
    public static JsonElement Apply(JsonElement value, IReadOnlyList<string> rules)
    {
        if (rules.Count == 0) return value;

        var node = JsonNode.Parse(value.GetRawText());
        foreach (var rule in rules)
        {
            var colon = rule.IndexOf(':');
            if (colon <= 0) throw new InvalidOperationException($"malformed normalize rule '{rule}'");
            var verb = rule[..colon];
            var field = rule[(colon + 1)..];
            switch (verb)
            {
                case "redact":
                    node = Walk(node, field, _ => JsonValue.Create("<redacted>"));
                    break;
                case "sort":
                    node = Walk(node, field, Sort);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"normalize rule '{rule}' is not implemented by this runner. "
                        + "Implement it or change the vector — an unimplemented rule silently "
                        + "weakens the comparison.");
            }
        }
        return JsonDocument.Parse(node?.ToJsonString() ?? "null").RootElement.Clone();
    }

    private static JsonNode? Sort(JsonNode? n)
    {
        if (n is not JsonArray array) return n;
        var items = array.Select(i => i?.DeepClone()).ToList();
        items.Sort((a, b) => string.CompareOrdinal(a?.ToJsonString() ?? "null", b?.ToJsonString() ?? "null"));
        return new JsonArray([.. items]);
    }

    private static JsonNode? Walk(JsonNode? node, string field, Func<JsonNode?, JsonNode?> edit)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (key, child) in obj.ToList())
                    result[key] = key == field ? edit(child?.DeepClone()) : Walk(child?.DeepClone(), field, edit);
                return result;
            }
            case JsonArray array:
                return new JsonArray([.. array.Select(i => Walk(i?.DeepClone(), field, edit))]);
            default:
                return node;
        }
    }
}
