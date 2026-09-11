using flow_weaver_backend.Services.Worker;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace flow_weaver_backend.Tests;

// Regression tests for the queue-tag override that routes python_snippet
// steps (`sandbox` tag) away from containers that cannot run bwrap.
//
// The trap these pin down: .NET's ConfigurationBinder can only ADD to a
// pre-initialized array — existing elements are preserved and config items
// appended — so a non-empty `Tags` initializer could never be SHRUNK by an
// operator override. The compose backend set Worker__Tags__0/1 to exclude
// `sandbox`, yet the bound array still contained it and the hardened
// container kept claiming python steps it cannot sandbox. Hence: the bound
// property defaults to empty and consumers read EffectiveTags.
public class WorkerOptionsTagsBindingTests
{
    private static WorkerOptions Bind(params (string Key, string Value)[] pairs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();
        var opts = new WorkerOptions();
        config.GetSection(WorkerOptions.SectionName).Bind(opts);
        return opts;
    }

    // Single-process setups (dev, or a deploy with no override) must claim
    // everything — including `sandbox` — or python steps would hang forever.
    [Fact]
    public void NoConfig_ClaimsAllTagsIncludingSandbox()
    {
        var opts = Bind();
        Assert.Equal(new[] { "default", "orchestrator", "sandbox" }, opts.EffectiveTags);
    }

    // The compose backend's exact override (Worker__Tags__0/__1 env vars →
    // Worker:Tags:0/1). The whole point is that `sandbox` must NOT survive:
    // with a non-empty array initializer the binder kept it and the fix
    // never took effect in the deploy.
    [Fact]
    public void IndexOverride_ExcludesSandbox()
    {
        var opts = Bind(("Worker:Tags:0", "default"), ("Worker:Tags:1", "orchestrator"));
        Assert.Equal(new[] { "default", "orchestrator" }, opts.EffectiveTags);
    }

    // A worker-only container could also be scoped down to just sandbox work.
    [Fact]
    public void IndexOverride_SingleTag()
    {
        var opts = Bind(("Worker:Tags:0", "sandbox"));
        Assert.Equal(new[] { "sandbox" }, opts.EffectiveTags);
    }

    // Duplicate entries (e.g. appsettings + env both contributing) must not
    // produce a duplicated claim list.
    [Fact]
    public void DuplicateEntries_AreDeduplicated()
    {
        var opts = Bind(
            ("Worker:Tags:0", "default"),
            ("Worker:Tags:1", "default"),
            ("Worker:Tags:2", "orchestrator"));
        Assert.Equal(new[] { "default", "orchestrator" }, opts.EffectiveTags);
    }

    // Empirical proof of the binder behavior the empty default guards
    // against: binding onto a NON-empty array keeps the initializer's
    // elements (config items are appended, nothing is removed). If this
    // ever starts failing, the binder changed and the EffectiveTags
    // indirection can be revisited.
    private sealed class ArrayDefaultProbe
    {
        public string[] Tags { get; set; } = ["a", "b", "c"];
    }

    [Fact]
    public void Binder_CannotShrinkNonEmptyArrayDefault()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Probe:Tags:0"] = "x",
                ["Probe:Tags:1"] = "y",
            })
            .Build();
        var probe = new ArrayDefaultProbe();
        config.GetSection("Probe").Bind(probe);

        // "c" survives the bind — this is exactly why WorkerOptions.Tags
        // must default to empty: an operator override could never have
        // removed `sandbox` from a pre-initialized default.
        Assert.Contains("c", probe.Tags);
    }
}
