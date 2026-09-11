using flow_weaver_backend.Services.Ai.Seed;
using flow_weaver_backend.Services.Worker.Handlers;

namespace flow_weaver_backend.Tests;

// The seeded `SSH primitive (paramiko)` body is the only baseline that is CODE rather
// than a declaration, so it is the only one that can be broken by an edit that still
// compiles. Two things have already tried to break it:
//
//   - naming the exec-mode helper `_run_exec`, because `_run_exec(` ends in `exec(`
//     and the pre-filter matches substrings, not call sites;
//   - `re.compile(...)`, because the AST guard blocks the NAME `compile` wherever it
//     appears, attribute access included.
//
// Neither is visible by reading the body. Both fail here instead of on a device.
public class ParamikoBaselineSnippetTests
{
    [Fact]
    public void The_seeded_body_passes_the_pre_filter_for_a_network_enabled_snippet()
    {
        Assert.Null(PythonHandler.CheckDangerousCode(
            DefaultSnippetsSeedService.ParamikoCode, networkEnabled: true));
    }

    [Fact]
    public void The_seeded_body_is_refused_without_the_network_flag()
    {
        // The flag on the row is load-bearing, not decoration: `paramiko` lives only in
        // the extended module set. If this ever passes, either the snippet stopped
        // importing paramiko or the base allow-list grew a network library.
        var error = PythonHandler.CheckDangerousCode(
            DefaultSnippetsSeedService.ParamikoCode, networkEnabled: false);

        Assert.NotNull(error);
        Assert.Contains("paramiko", error);
    }
}
