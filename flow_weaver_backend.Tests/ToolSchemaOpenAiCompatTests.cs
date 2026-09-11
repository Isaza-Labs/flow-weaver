using System.Runtime.CompilerServices;
using System.Text.Json;
using flow_weaver_backend.Services.Ai.Tools;

namespace flow_weaver_backend.Tests;

// Guards that agent tool parameter schemas satisfy OpenAI's function-calling
// constraints: the top-level schema must be `type: "object"` and must NOT carry
// oneOf/anyOf/allOf/enum/const/not at the top level. A violation is not caught
// at build time — it surfaces as a 400 invalid_function_parameters that breaks
// EVERY chat turn (the whole tool array is rejected). This test breaks first.
//
// Coverage note: it reads each schema via GetUninitializedObject, which works for
// the getter-style (`ParametersSchema => JsonDocument.Parse(...)`) handlers — the
// majority, and where richer schemas (the ones prone to this) live. A dozen
// handlers expose the schema as a field initializer (`{ get; } = ...`) that the
// uninitialized instance can't surface; those are skipped (ValueKind != Object).
public class ToolSchemaOpenAiCompatTests
{
    private static readonly string[] ForbiddenTopLevel =
        { "anyOf", "oneOf", "allOf", "enum", "const", "not" };

    [Fact]
    public void Every_readable_tool_schema_is_openai_compatible()
    {
        var violations = new List<string>();
        var validated = 0;

        foreach (var type in AgentToolHandlers.All)
        {
            var handler = (IToolHandler)RuntimeHelpers.GetUninitializedObject(type);

            JsonElement schema;
            try { schema = handler.ParametersSchema; }
            catch { continue; }                              // getter needs ctor state — skip
            if (schema.ValueKind != JsonValueKind.Object) continue;  // field-initialized — not readable here
            validated++;

            var name = SafeName(handler, type);

            var typeOk = schema.TryGetProperty("type", out var t)
                && t.ValueKind == JsonValueKind.String
                && string.Equals(t.GetString(), "object", StringComparison.Ordinal);
            if (!typeOk)
                violations.Add($"{name}: top-level \"type\" must be \"object\"");

            foreach (var kw in ForbiddenTopLevel)
                if (schema.TryGetProperty(kw, out _))
                    violations.Add($"{name}: top-level \"{kw}\" is rejected by OpenAI function-calling "
                        + "(move the constraint into runtime validation)");
        }

        // Non-vacuous: if the reflection approach ever stops surfacing schemas,
        // fail rather than silently pass on zero.
        Assert.True(validated >= 30,
            $"expected to read >=30 tool schemas via reflection, only read {validated}");
        Assert.True(violations.Count == 0, "OpenAI-incompatible tool schemas:\n" + string.Join("\n", violations));
    }

    private static string SafeName(IToolHandler handler, Type type)
    {
        try { return handler.Name; } catch { return type.Name; }
    }
}
