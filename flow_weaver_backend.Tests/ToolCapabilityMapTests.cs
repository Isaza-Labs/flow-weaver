using flow_weaver_backend.Services.Ai.Permissions;
using flow_weaver_backend.Services.Permission.Catalog;

namespace flow_weaver_backend.Tests;

// Phase 5 of the RBAC-granular refactor (plan_rbac_granular.md): the agent tool
// gate. Guards that every classified tool has a capability mapping (so a new
// tool can't silently become default-denied in granular mode) and that every
// mapping points at a real catalogue capability.
public class ToolCapabilityMapTests
{
    [Fact]
    public void Every_classified_tool_has_a_capability_mapping()
    {
        var unmapped = PermissionClassifier.Matrix.Keys
            .Where(tool => ToolCapabilityMap.For(tool) is null)
            .OrderBy(t => t)
            .ToList();

        Assert.True(unmapped.Count == 0,
            "tools in PermissionClassifier.Matrix with no ToolCapabilityMap entry "
            + "(they would be default-denied in granular mode): " + string.Join(", ", unmapped));
    }

    [Fact]
    public void Every_mapped_capability_exists_in_the_catalogue()
    {
        var bad = ToolCapabilityMap.Tools
            .Select(tool => (tool, cap: ToolCapabilityMap.For(tool)!))
            .Where(x => !CapabilityCatalog.IsKnown(x.cap))
            .Select(x => $"{x.tool} → {x.cap}")
            .ToList();

        Assert.True(bad.Count == 0, "tool mappings pointing at unknown capabilities: " + string.Join(", ", bad));
    }
}
