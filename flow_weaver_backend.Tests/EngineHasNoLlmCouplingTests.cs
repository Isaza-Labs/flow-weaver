using System.Reflection;
using System.Runtime.CompilerServices;
using flow_weaver_backend.Services.Engine;

namespace flow_weaver_backend.Tests;

// Traceability: FR-009 (TC-FW-009) / NFR-005 (TC-FW-031) — the execution engine
// never invokes an LLM. Rather than instrument a run, we assert the structural
// precondition that makes an LLM call impossible: no type under
// flow_weaver_backend.Services.Engine has a compile-time dependency
// (constructor parameter or field) on the AI/LLM layer
// (flow_weaver_backend.Services.Ai.*). If the engine can't hold a reference to
// a provider, a run can't call one.
public class EngineHasNoLlmCouplingTests
{
    private const string EngineNs = "flow_weaver_backend.Services.Engine";
    private const string AiNs = "flow_weaver_backend.Services.Ai";

    [Fact]
    public void Engine_types_have_no_dependency_on_the_ai_layer()
    {
        var asm = typeof(WorkflowExecutor).Assembly;

        var engineTypes = asm.GetTypes().Where(t =>
            t.Namespace is not null
            && (t.Namespace == EngineNs || t.Namespace.StartsWith(EngineNs + "."))
            && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false));

        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        var offenders = new List<string>();
        foreach (var t in engineTypes)
        {
            foreach (var ctor in t.GetConstructors(Flags))
                foreach (var p in ctor.GetParameters())
                    if (ReferencesAi(p.ParameterType))
                        offenders.Add($"{t.FullName}(.ctor) param {p.Name}: {p.ParameterType.Name}");

            foreach (var f in t.GetFields(Flags))
                if (ReferencesAi(f.FieldType))
                    offenders.Add($"{t.FullName}.{f.Name}: {f.FieldType.Name}");
        }

        Assert.True(offenders.Count == 0,
            "The execution engine must not depend on the AI/LLM layer. Offenders:\n"
            + string.Join("\n", offenders));
    }

    // True if the type — or any of its generic arguments — lives under
    // flow_weaver_backend.Services.Ai.
    private static bool ReferencesAi(Type type)
    {
        if (type.Namespace is not null
            && (type.Namespace == AiNs || type.Namespace.StartsWith(AiNs + ".")))
            return true;
        return type.IsGenericType && type.GetGenericArguments().Any(ReferencesAi);
    }
}
