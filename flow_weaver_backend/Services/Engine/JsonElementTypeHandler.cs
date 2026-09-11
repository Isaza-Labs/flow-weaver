using System.Data;
using System.Text.Json;
using Dapper;

namespace flow_weaver_backend.Services.Engine;

// Dapper doesn't know how to round-trip System.Text.Json.JsonElement. This
// handler serializes to the raw JSON text when writing a parameter (the SQL
// sites that use this cast with ::jsonb so Postgres stores it in the right
// column type) and re-parses the string row values into a JsonDocument on
// read.
//
// Registered once at startup so every Dapper query in the project sees the
// same mapping — matches the EF Core ValueConverter behavior in
// AppDbContext.
public sealed class JsonElementTypeHandler : SqlMapper.TypeHandler<JsonElement>
{
    public override JsonElement Parse(object value)
    {
        if (value is string s && !string.IsNullOrEmpty(s))
            return JsonDocument.Parse(s).RootElement;
        return JsonDocument.Parse("null").RootElement;
    }

    public override void SetValue(IDbDataParameter parameter, JsonElement value)
    {
        parameter.Value = value.ValueKind == JsonValueKind.Undefined
            ? "null"
            : value.GetRawText();
        parameter.DbType = DbType.String;
    }
}
