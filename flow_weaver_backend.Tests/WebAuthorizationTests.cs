using System.Reflection;
using System.Security.Claims;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Permission.Catalog;
using flow_weaver_backend.Services.Security.Authorization;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Tests;

// Phase 4 of the RBAC-granular refactor (plan_rbac_granular.md): the web
// enforcement layer. Guards that controllers migrated off the legacy
// Viewer/Operator policies, that every [HasPermission] names a real capability,
// and that the handler reproduces legacy tiers (RbacMode=legacy) while honouring
// grants (RbacMode=granular) with admin always bypassing.
public class WebAuthorizationTests
{
    private static IReadOnlyList<Type> Controllers() =>
        typeof(CapabilityCatalog).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t)
                        && !t.IsAbstract
                        && t.Namespace == "flow_weaver_backend.Controllers")
            .ToList();

    private static IEnumerable<(string Where, string? Policy)> AllPolicies(Type t)
    {
        foreach (var a in t.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            yield return ($"{t.Name}(class)", a.Policy);
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            foreach (var a in m.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
                yield return ($"{t.Name}.{m.Name}", a.Policy);
    }

    [Fact]
    public void No_controller_uses_the_legacy_viewer_or_operator_policy()
    {
        var offenders = Controllers()
            .SelectMany(AllPolicies)
            .Where(p => p.Policy is "Viewer" or "Operator")
            .Select(p => $"{p.Where} → Policy=\"{p.Policy}\"")
            .OrderBy(s => s)
            .ToList();

        Assert.True(offenders.Count == 0,
            "these endpoints still use the legacy Viewer/Operator policy instead of [HasPermission]:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void Every_hasPermission_capability_exists_in_the_catalogue()
    {
        var unknown = Controllers()
            .SelectMany(AllPolicies)
            .Where(p => p.Policy is not null
                        && p.Policy.StartsWith(HasPermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
            .Select(p => (p.Where, Cap: p.Policy![HasPermissionAttribute.PolicyPrefix.Length..]))
            .Where(x => !CapabilityCatalog.IsKnown(x.Cap))
            .Select(x => $"{x.Where} → '{x.Cap}'")
            .ToList();

        Assert.True(unknown.Count == 0,
            "[HasPermission] capabilities not present in the catalogue:\n" + string.Join("\n", unknown));
    }

    // ── handler behaviour ─────────────────────────────────────────────────

    private static async Task<bool> Decide(string[] roles, string mode, IEffectivePermissions eff, string cap)
    {
        var handler = new PermissionAuthorizationHandler(
            new FakeUser { Roles = roles }, eff, new StubSettings(mode));
        var requirement = new PermissionRequirement(cap);
        var ctx = new AuthorizationHandlerContext(
            new[] { requirement },
            new ClaimsPrincipal(new ClaimsIdentity("jwt")),   // authenticated identity
            resource: null);
        await handler.HandleAsync(ctx);
        return ctx.HasSucceeded;
    }

    [Theory]
    [InlineData("secret.manage")]
    [InlineData("workflow.run")]
    [InlineData("unknown.capability")]
    public async Task Admin_bypasses_every_requirement_in_both_modes(string cap)
    {
        Assert.True(await Decide(new[] { "admin" }, RbacModes.Legacy, new StubEffective(), cap));
        Assert.True(await Decide(new[] { "admin" }, RbacModes.Granular, new StubEffective(), cap));
    }

    [Fact]
    public async Task Legacy_mode_reproduces_the_capability_tier()
    {
        // Operator holds Viewer+Operator tiers, not Admin.
        Assert.True(await Decide(new[] { "operator" }, RbacModes.Legacy, new StubEffective(), "workflow.read"));
        Assert.True(await Decide(new[] { "operator" }, RbacModes.Legacy, new StubEffective(), "workflow.update"));
        Assert.False(await Decide(new[] { "operator" }, RbacModes.Legacy, new StubEffective(), "secret.manage"));

        // Viewer holds only the Viewer tier.
        Assert.True(await Decide(new[] { "viewer" }, RbacModes.Legacy, new StubEffective(), "workflow.read"));
        Assert.False(await Decide(new[] { "viewer" }, RbacModes.Legacy, new StubEffective(), "workflow.update"));
    }

    [Fact]
    public async Task Granular_mode_consults_the_grants_not_the_role()
    {
        // A "viewer"-role user whose grants happen to include workflow.update
        // (a custom grant) is allowed; the role string is irrelevant.
        var eff = new StubEffective("workflow.read", "workflow.update");
        Assert.True(await Decide(new[] { "viewer" }, RbacModes.Granular, eff, "workflow.update"));
        Assert.False(await Decide(new[] { "viewer" }, RbacModes.Granular, eff, "workflow.delete"));
    }

    [Fact]
    public async Task Transport_ceiling_caps_admin_in_the_handler()
    {
        var admin = new FakeUser { Roles = new[] { "admin" }, CapabilityCeiling = new[] { "workflow.read" } };

        var handler = new PermissionAuthorizationHandler(admin, new StubEffective(), new StubSettings(RbacModes.Granular));
        Assert.True(await Run(handler, "workflow.read"));    // inside the ceiling
        Assert.False(await Run(handler, "secret.manage"));   // admin, but outside the ceiling
    }

    private static async Task<bool> Run(PermissionAuthorizationHandler handler, string cap)
    {
        var ctx = new AuthorizationHandlerContext(
            new[] { new PermissionRequirement(cap) },
            new ClaimsPrincipal(new ClaimsIdentity("jwt")),
            resource: null);
        await handler.HandleAsync(ctx);
        return ctx.HasSucceeded;
    }

    private sealed class StubEffective : IEffectivePermissions
    {
        private readonly IReadOnlySet<string> _caps;
        public StubEffective(params string[] caps) =>
            _caps = new HashSet<string>(caps, StringComparer.OrdinalIgnoreCase);

        public Task<bool> HasAsync(string c, PermissionContext ctx, CancellationToken ct = default)
            => Task.FromResult(_caps.Contains(c));
        public Task<bool> HasAsync(string c, CancellationToken ct = default)
            => Task.FromResult(_caps.Contains(c));
        public Task<IReadOnlySet<string>> CapabilitiesAsync(CancellationToken ct = default)
            => Task.FromResult(_caps);
    }

    private sealed class StubSettings : IAppSettingsService
    {
        private readonly string _mode;
        public StubSettings(string mode) => _mode = mode;
        public Task<AppSettings> GetAsync(CancellationToken ct = default)
            => Task.FromResult(new AppSettings { RbacMode = _mode });
        public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default)
            => Task.FromResult(updated);
    }
}
