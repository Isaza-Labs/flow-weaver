using System.Runtime.CompilerServices;
using flow_weaver_backend.Services.Ai.Permissions;
using flow_weaver_backend.Services.Ai.Tools;

namespace flow_weaver_backend.Tests;

// FR-037 / TC-FW-063 (DEF-005) — the classification-coverage guard. Iterates the
// EXACT set of tool handlers registered into the ToolRegistry at startup
// (AgentToolHandlers.All, the single source of truth Program.cs also uses) and
// contrasts every one against PermissionClassifier.Matrix and ToolCapabilityMap.
//
// The whole point (criterion d): this test BREAKS the moment someone registers a
// tool without classifying it — so a tool can never silently rely on the
// unknown-fallback (human_only) or become default-denied in granular mode.
public class ToolClassificationCoverageTests
{
    // Every registered handler exposes its tool name via a constant getter, so
    // we can read it without constructing the (heavily-DI'd) handler.
    private static string ToolName(Type handlerType)
    {
        var handler = (IToolHandler)RuntimeHelpers.GetUninitializedObject(handlerType);
        return handler.Name;
    }

    public static IEnumerable<object[]> RegisteredTools =>
        AgentToolHandlers.All.Select(t => new object[] { ToolName(t) });

    [Fact]
    public void Registry_is_non_empty()
    {
        // Guards the reflection itself: if the list ever resolves empty the
        // per-tool assertions below would vacuously pass.
        Assert.NotEmpty(AgentToolHandlers.All);
        Assert.All(AgentToolHandlers.All, t => Assert.False(string.IsNullOrWhiteSpace(ToolName(t))));
    }

    // (a) + (b) + (d): every registered tool has an EXPLICIT PermissionClassifier
    // entry — never the ('unknown','dangerous',human_only) fallback by omission.
    [Fact]
    public void Every_registered_tool_has_an_explicit_classifier_entry()
    {
        var unclassified = AgentToolHandlers.All
            .Select(ToolName)
            .Where(name => !PermissionClassifier.Matrix.ContainsKey(name))
            .OrderBy(n => n)
            .ToList();

        Assert.True(unclassified.Count == 0,
            "registered tools with NO explicit PermissionClassifier.Matrix entry "
            + "(they fall back to the human_only 'unknown' tier — classify them): "
            + string.Join(", ", unclassified));
    }

    // (d) for the granular system: every registered tool has a ToolCapabilityMap
    // entry, else it is default-denied under RbacMode=granular.
    [Fact]
    public void Every_registered_tool_has_a_capability_mapping()
    {
        var unmapped = AgentToolHandlers.All
            .Select(ToolName)
            .Where(name => ToolCapabilityMap.For(name) is null)
            .OrderBy(n => n)
            .ToList();

        Assert.True(unmapped.Count == 0,
            "registered tools with NO ToolCapabilityMap entry "
            + "(default-denied in granular mode — map them to a capability): "
            + string.Join(", ", unmapped));
    }

    // The two tables must describe the same set of names.
    //
    // NOT "every classification names a registered tool" — that fails by design, and
    // deliberately so: forward-looking entries are the documented reason the matrix is
    // larger than the registry. What has no design behind it is a name in one table and
    // not the other, because under RbacMode=granular that is a tool classified but not
    // grantable, or grantable but unclassified. They agree today at 77 each; this is
    // what keeps that true.
    [Fact]
    public void The_two_classification_tables_name_the_same_tools()
    {
        var matrix = PermissionClassifier.Matrix.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var capabilities = ToolCapabilityMap.Tools.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matrixOnly = matrix.Except(capabilities, StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        var capabilityOnly = capabilities.Except(matrix, StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();

        Assert.True(matrixOnly.Count == 0 && capabilityOnly.Count == 0,
            "PermissionClassifier.Matrix and ToolCapabilityMap must name the same tools. "
            + $"Only in the matrix: {string.Join(", ", matrixOnly)}. "
            + $"Only in the capability map: {string.Join(", ", capabilityOnly)}.");
    }

    // Every registered tool resolves through the derived name set the matrix endpoint
    // uses to report `implemented`. Guards the reflection in AgentToolHandlers: if it
    // ever returned an empty or partial set, the endpoint would quietly mark every row
    // unimplemented and nothing else would notice.
    [Fact]
    public void Registered_tool_names_match_the_registry()
    {
        var derived = AgentToolHandlers.RegisteredToolNames;
        var expected = AgentToolHandlers.All.Select(ToolName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(derived);
        Assert.True(derived.SetEquals(expected),
            "AgentToolHandlers.RegisteredToolNames must equal the names of AgentToolHandlers.All.");
    }

    // (c): no execute-level tool is autonomous. `execute` means the tool
    // actually runs something with side effects (live device runs, report
    // generation, MCP/REST calls, plan builds), so it must require at least a
    // confirmation tier. NOTE `dangerous` is deliberately NOT included here — in
    // this matrix `dangerous` is the ROLE sensitivity (admin-only), not
    // "destructive": read-only-but-admin tools like list_users / list_policies
    // are (dangerous, autonomous) on purpose. The actually-destructive dangerous
    // tools (delete_*, integration_execute) are human_only, and mutating
    // dangerous tools (create_user, set_user_role) are elevated_confirm — all
    // already caught by being non-autonomous.
    [Theory]
    [MemberData(nameof(RegisteredTools))]
    public void No_execute_level_tool_is_autonomous(string toolName)
    {
        if (!PermissionClassifier.Matrix.TryGetValue(toolName, out var perm))
            return; // coverage is asserted by the dedicated test above.

        var autonomous = string.Equals(perm.Tier, PermissionClassifier.TierAutonomous, StringComparison.OrdinalIgnoreCase);

        Assert.False(perm.Level == "execute" && autonomous,
            $"tool '{toolName}' is level 'execute' but tier 'autonomous' — a "
            + "side-effecting action must not run without a confirmation tier.");
    }

    // The two tools TC-FW-063 flagged as unclassified must now be explicitly
    // present in BOTH systems — pins the fix so a future edit can't quietly drop
    // them back to the fallback.
    [Theory]
    [InlineData("analyze_foreign_workflow")]
    [InlineData("generate_snippet_for_import")]
    public void Def_005_tools_are_explicitly_classified_in_both_systems(string toolName)
    {
        Assert.True(PermissionClassifier.Matrix.ContainsKey(toolName), $"{toolName} missing from PermissionClassifier.Matrix");
        Assert.NotNull(ToolCapabilityMap.For(toolName));
    }
}
