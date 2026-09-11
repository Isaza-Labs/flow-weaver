using System.Text.Json;
using System.Text.Json.Serialization;

namespace flow_weaver_backend.Services.Security;

// System.Text.Json throws "Operation is not valid due to the current state
// of the object" when asked to serialize a JsonElement whose ValueKind is
// Undefined. That happens anywhere a service does `dto.Something ?? default`
// and the caller never supplied Something: the default(JsonElement) is
// Undefined, the DB converter writes it as "null" fine, but the HTTP
// response serializer blows up before the client ever sees the row.
//
// We intercept at the serializer: Undefined → JSON null. Reads stay on the
// framework's default path so deserialising request bodies still works
// exactly as before.
public class SafeJsonElementConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return doc.RootElement.Clone();
    }

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
            return;
        }
        value.WriteTo(writer);
    }
}
