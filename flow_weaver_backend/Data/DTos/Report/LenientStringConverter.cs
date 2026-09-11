using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Dtos;

// Accepts any JSON scalar (string / number / boolean / null) and
// materializes it as a string. Used on fields that are display-only
// in the final report (stat values, table cells, etc.) so authors can
// write `"value": 0` or `"value": true` without wrapping in quotes.
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadScalar(ref reader);

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    internal static string ReadScalar(ref Utf8JsonReader reader) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => string.Empty,
            _ => throw new JsonException(
                $"unsupported token {reader.TokenType} for lenient string"),
        };
}

// Row-level variant: a list of lists of lenient strings. Applied to
// `ReportTable.Rows` so each cell can arrive as any JSON scalar.
public sealed class LenientStringRowsConverter : JsonConverter<List<List<string>>>
{
    public override List<List<string>> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return new();
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("rows must be a JSON array");

        var rows = new List<List<string>>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("each row must be a JSON array");

            var cells = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                cells.Add(LenientStringConverter.ReadScalar(ref reader));
            }
            rows.Add(cells);
        }
        return rows;
    }

    public override void Write(
        Utf8JsonWriter writer, List<List<string>> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var row in value)
        {
            writer.WriteStartArray();
            foreach (var cell in row) writer.WriteStringValue(cell);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}
