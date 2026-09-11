using flow_weaver_backend.Services.Ai.Permissions;
using Microsoft.Extensions.Logging.Abstractions;

namespace flow_weaver_backend.Tests;

// Locks the RBAC classification of the agent's configuration tools —
// the gate that makes "the agent configures the app respecting the caller's
// role" hold. ToolDispatcher runs PermissionClassifier.IsAllowed BEFORE a
// handler executes, so these classifications ARE the enforcement.
public class ConfigToolsPermissionTests
{
    private static readonly PermissionClassifier Classifier =
        new(NullLogger<PermissionClassifier>.Instance);

    [Theory]
    [InlineData("create_user")]
    [InlineData("list_users")]
    [InlineData("set_user_role")]
    [InlineData("grant_resource_permission")]
    [InlineData("create_policy")]
    public void Admin_only_config_tools_require_admin(string tool)
    {
        Assert.True(Classifier.IsAllowed(tool, "admin"), $"{tool} should be allowed for admin");
        Assert.False(Classifier.IsAllowed(tool, "operator"), $"{tool} must be denied for operator");
        Assert.False(Classifier.IsAllowed(tool, "viewer"), $"{tool} must be denied for viewer");
    }

    [Theory]
    [InlineData("create_vendor_command")]
    [InlineData("update_vendor_command")]
    [InlineData("delete_vendor_command")]
    public void Vendor_command_writes_require_operator(string tool)
    {
        // Matches the VendorCommandController's Operator gate.
        Assert.True(Classifier.IsAllowed(tool, "admin"));
        Assert.True(Classifier.IsAllowed(tool, "operator"));
        Assert.False(Classifier.IsAllowed(tool, "viewer"), $"{tool} must be denied for viewer");
    }

    [Fact]
    public void List_policies_is_readable_by_every_role()
    {
        Assert.True(Classifier.IsAllowed("list_policies", "viewer"));
        Assert.True(Classifier.IsAllowed("list_policies", "operator"));
        Assert.True(Classifier.IsAllowed("list_policies", "admin"));
    }

    [Theory]
    [InlineData("create_user")]
    [InlineData("set_user_role")]
    [InlineData("grant_resource_permission")]
    [InlineData("create_policy")]
    [InlineData("create_vendor_command")]
    [InlineData("update_vendor_command")]
    [InlineData("delete_vendor_command")]
    public void Config_tools_are_agent_executable_not_human_only(string tool)
    {
        // human_only tools are refused by the dispatcher even for admins. The
        // config tools must NOT be human_only, or an authorized user's agent
        // could never run them (the whole point of the feature).
        var perm = Classifier.GetPermission(tool);
        Assert.NotEqual(PermissionClassifier.TierHumanOnly, perm.Tier);
    }

    [Fact]
    public void Config_tools_are_classified_not_unknown()
    {
        // A tool missing from the matrix falls back to unknown/dangerous/
        // human_only (hard-deny). Assert every new tool is explicitly present.
        foreach (var tool in new[]
        {
            "create_user", "list_users", "set_user_role", "grant_resource_permission",
            "create_policy", "list_policies",
            "create_vendor_command", "update_vendor_command", "delete_vendor_command",
        })
        {
            Assert.NotEqual("unknown", Classifier.GetPermission(tool).Domain);
        }
    }
}
