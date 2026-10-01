using System.Text.Json;

namespace flow_weaver_backend.Services.Ai.Secrets;

// `${secret:...}` references are resolved to plaintext by the worker handlers
// (PythonHandler walks its whole payload; SSH and REST resolve credentials,
// URLs and headers). That is only safe for references someone was allowed to
// write into the workflow. Data that arrives while a run executes — the run
// input (manual input, webhook bodies, trigger input_defaults), upstream step
// outputs (device and API responses) and the device / run context — must never
// be able to name a secret, or anyone who can start a run, or any system whose
// response a step reads, could get a secret decrypted into a step payload.
//
// Neutralize inserts a WORD JOINER (U+2060) between `$` and `{`. The text reads
// the same, but SecretResolver's `\$\{secret:` pattern no longer matches it.
public static class SecretMarkers
{
    public const string Marker = "${secret:";
    private const string Neutralized = "$⁠{secret:";

    public static bool Contains(string? value)
        => value is not null && value.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    public static string Neutralize(string value)
        => Contains(value)
            ? System.Text.RegularExpressions.Regex.Replace(
                value, @"\$\{secret:", Neutralized,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : value;

    // Returns the same element when nothing needs changing, so the common case
    // costs one scan and no allocation.
    public static JsonElement Neutralize(JsonElement element)
    {
        if (!ContainsAny(element)) return element;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
            Write(writer, element);
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    public static bool ContainsAny(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return Contains(element.GetString());
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    if (ContainsAny(prop.Value)) return true;
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (ContainsAny(item)) return true;
                return false;
            default:
                return false;
        }
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStringValue(Neutralize(element.GetString() ?? string.Empty));
                break;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    Write(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
