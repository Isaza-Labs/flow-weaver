using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace flow_weaver_backend.Services.Workflow;

/// <summary>
/// The canonical text form of a workflow graph, and the <c>schema_hash</c> derived from it.
/// </summary>
/// <remarks>
/// This value is the IDENTITY of a graph. A <c>SimulationResult</c> row is matched to the graph
/// it simulated by comparing it, and a promotion gate that has been passed stays passed only
/// while it keeps agreeing — so a change here silently invalidates stored simulations and
/// re-opens closed gates.
///
/// It is also the one part of the engine that is already byte-for-byte equivalent to the reference
/// engine's, which is why this file keeps the same name, type name and public surface:
/// the parity programme's
/// first entry in a shared file map, and the cheapest kind — a relocation, not a rewrite.
///
/// Moved here verbatim from <c>Services/Ai/Tools/Handlers/SimulateWorkflowRunHandler</c>, where
/// it had been filed under an assistant feature while promotion and the workflow gate both
/// depended on it. Behaviour is unchanged and <c>CanonicalHashGoldenTests</c> is what says so:
/// it pins the hashes of a fixed corpus captured BEFORE this move.
/// </remarks>
public static class WorkflowCanonicalizer
{
    public static string ComputeSchemaHash(JsonElement nodes, JsonElement edges)
    {
        var sb = new StringBuilder();
        WriteCanonical(sb, nodes);
        sb.Append('|');
        WriteCanonical(sb, edges);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void WriteCanonical(StringBuilder sb, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Number:
                // Normalize numeric forms so `1`, `1.0`, and `1.00` hash
                // identically — Postgres jsonb strips trailing zeros on
                // storage, which would otherwise let an edit-then-store
                // round-trip silently change the canonical text. Prefer
                // decimal for precision; fall back to double for values
                // outside decimal's range. System.Decimal preserves
                // trailing zeros on ToString, so we strip them manually.
                if (el.TryGetDecimal(out var dec))
                    sb.Append(StripTrailingZeros(dec.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                else if (el.TryGetDouble(out var dbl))
                    sb.Append(dbl.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                else
                    sb.Append(el.GetRawText());
                break;
            case JsonValueKind.String:
                sb.Append(JsonSerializer.Serialize(el.GetString()));
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    WriteCanonical(sb, item);
                    firstItem = false;
                }
                sb.Append(']');
                break;
            case JsonValueKind.Object:
                sb.Append('{');
                var props = el.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                for (var i = 0; i < props.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonSerializer.Serialize(props[i].Name));
                    sb.Append(':');
                    WriteCanonical(sb, props[i].Value);
                }
                sb.Append('}');
                break;
        }
    }


    /// <summary>Canonical serialization of a single JSON value, without hashing it.</summary>
    /// <remarks>Exposed for the conformance adapter, which asserts the canonical FORM as well
    /// as the digest: two graphs hashing alike says less than two graphs canonicalizing alike,
    /// and a bug that collapsed everything to one string would satisfy the first.</remarks>
    public static string Canonical(JsonElement value)
    {
        var sb = new StringBuilder();
        WriteCanonical(sb, value);
        return sb.ToString();
    }

    private static string StripTrailingZeros(string s)
    {
        if (!s.Contains('.')) return s;
        s = s.TrimEnd('0');
        if (s.EndsWith('.')) s = s[..^1];
        return s.Length == 0 ? "0" : s;
    }
}
