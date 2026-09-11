using System.Reflection;
using System.Text.Json;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using StepRunModel = flow_weaver_backend.Models.StepRun;

namespace flow_weaver_backend.Tests;

// The payload-shaping helpers inside WorkflowExecutor. They are private
// statics, so they are reached by reflection (same approach as
// ExecutionRobustnessTests) — the alternative is standing up the whole
// orchestrator to observe a JSON envelope.
//
// This is the contract every workflow author templates against
// (`{{ steps.X.output.devices_by_name['rtr-1'].output.stdout }}`), so a change
// in shape silently breaks live workflows. Pinning it here makes that visible.
public class WorkflowExecutorPayloadTests
{
    private static readonly Type Executor = typeof(WorkflowExecutor);

    private static MethodInfo Method(string name)
        => Executor.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
           ?? throw new InvalidOperationException($"{name} not found — was it renamed?");

    private static JsonElement Invoke(string name, params object?[] args)
        => (JsonElement)Method(name).Invoke(null, args)!;

    // ─── MergePayloads ──────────────────────────────────────────────────

    private static JsonElement Merge(string runInput, string overrides)
        => Invoke("MergePayloads", TestJson.Element(runInput), TestJson.Element(overrides));

    [Fact]
    public void Merge_UnionsBothObjects()
    {
        var merged = Merge("""{"a":1}""", """{"b":2}""");

        Assert.Equal(1, merged.GetProperty("a").GetInt32());
        Assert.Equal(2, merged.GetProperty("b").GetInt32());
    }

    // config_overrides is the node-level setting, so it must win over the
    // run-wide input.
    [Fact]
    public void Merge_ConfigOverridesWinOnCollision()
    {
        var merged = Merge("""{"host":"from-run"}""", """{"host":"from-node"}""");

        Assert.Equal("from-node", merged.GetProperty("host").GetString());
    }

    [Fact]
    public void Merge_NonObjectOverridesFallBackToRunInput()
    {
        var merged = Merge("""{"a":1}""", "null");

        Assert.Equal(1, merged.GetProperty("a").GetInt32());
    }

    [Fact]
    public void Merge_NonObjectRunInputFallsBackToOverrides()
    {
        var merged = Merge("[]", """{"b":2}""");

        Assert.Equal(2, merged.GetProperty("b").GetInt32());
    }

    [Fact]
    public void Merge_BothNonObjectsYieldAnEmptyObject()
    {
        var merged = Merge("null", "null");

        Assert.Equal(JsonValueKind.Object, merged.ValueKind);
        Assert.Empty(merged.EnumerateObject());
    }

    [Fact]
    public void Merge_PreservesNestedStructures()
    {
        var merged = Merge("""{"cfg":{"deep":[1,2]}}""", """{"other":true}""");

        Assert.Equal(2, merged.GetProperty("cfg").GetProperty("deep").GetArrayLength());
    }

    // ─── InjectTargets ──────────────────────────────────────────────────

    [Fact]
    public void InjectTargets_AddsTheTargetListAndKeepsThePayload()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var result = Invoke("InjectTargets", TestJson.Element("""{"cmd":"show ver"}"""), new List<Guid> { a, b });

        Assert.Equal("show ver", result.GetProperty("cmd").GetString());
        var targets = result.GetProperty("_targets").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Equal(new[] { a.ToString(), b.ToString() }, targets);
    }

    [Fact]
    public void InjectTargets_NonObjectPayloadStillProducesAnObject()
    {
        var result = Invoke("InjectTargets", TestJson.Element("null"), new List<Guid>());

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Empty(result.GetProperty("_targets").EnumerateArray());
    }

    // ─── InjectDevice ───────────────────────────────────────────────────

    [Fact]
    public void InjectDevice_StampsTheDeviceContext()
    {
        var result = Invoke("InjectDevice",
            TestJson.Element("""{"cmd":"x"}"""), TestJson.Element("""{"name":"rtr-1"}"""));

        Assert.Equal("rtr-1", result.GetProperty("device").GetProperty("name").GetString());
        Assert.Equal("x", result.GetProperty("cmd").GetString());
    }

    // An author who set `device` explicitly in config_overrides wins — we do
    // not clobber their intent.
    [Fact]
    public void InjectDevice_AuthorSuppliedDeviceIsLeftAlone()
    {
        var result = Invoke("InjectDevice",
            TestJson.Element("""{"device":{"name":"mine"}}"""), TestJson.Element("""{"name":"injected"}"""));

        Assert.Equal("mine", result.GetProperty("device").GetProperty("name").GetString());
    }

    [Fact]
    public void InjectDevice_NonObjectPayloadStillGetsTheDevice()
    {
        var result = Invoke("InjectDevice", TestJson.Element("null"), TestJson.Element("""{"name":"rtr-1"}"""));

        Assert.Equal("rtr-1", result.GetProperty("device").GetProperty("name").GetString());
    }

    // ─── InjectSteps ────────────────────────────────────────────────────

    private static JsonElement InjectSteps(string payload, Dictionary<string, StepResult> steps)
        => Invoke("InjectSteps", TestJson.Element(payload), steps);

    // python_snippet authors read upstream outputs as
    // inp['steps']['<node>']['output'] — that nesting is the contract.
    [Fact]
    public void InjectSteps_NestsEachOutputUnderItsNodeId()
    {
        var steps = new Dictionary<string, StepResult>
        {
            ["ssh-show-version"] = new(TestJson.Element("""{"stdout":"IOS 15"}""")),
        };

        var result = InjectSteps("""{"cmd":"x"}""", steps);

        Assert.Equal("IOS 15",
            result.GetProperty("steps").GetProperty("ssh-show-version")
                  .GetProperty("output").GetProperty("stdout").GetString());
        Assert.Equal("x", result.GetProperty("cmd").GetString());
    }

    [Fact]
    public void InjectSteps_UndefinedOutputBecomesJsonNull()
    {
        var steps = new Dictionary<string, StepResult> { ["a"] = new(default) };

        var result = InjectSteps("{}", steps);

        Assert.Equal(JsonValueKind.Null,
            result.GetProperty("steps").GetProperty("a").GetProperty("output").ValueKind);
    }

    // An author who built their own `steps` key keeps it.
    [Fact]
    public void InjectSteps_AuthorSuppliedStepsWins()
    {
        var steps = new Dictionary<string, StepResult> { ["a"] = new(TestJson.Element("1")) };

        var result = InjectSteps("""{"steps":"mine"}""", steps);

        Assert.Equal("mine", result.GetProperty("steps").GetString());
    }

    [Fact]
    public void InjectSteps_EmptyMapYieldsAnEmptyStepsObject()
    {
        var result = InjectSteps("{}", new Dictionary<string, StepResult>());

        Assert.Empty(result.GetProperty("steps").EnumerateObject());
    }

    // ─── CloneJson ──────────────────────────────────────────────────────

    // The live workflow's JsonDocument is disposed when the enqueue scope ends,
    // so snapshots must be detached copies.
    [Fact]
    public void CloneJson_SurvivesTheSourceDocumentBeingDisposed()
    {
        JsonElement clone;
        using (var doc = JsonDocument.Parse("""{"nodes":[{"id":"a"}]}"""))
        {
            clone = Invoke("CloneJson", doc.RootElement);
        }

        Assert.Equal("a", clone.GetProperty("nodes")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void CloneJson_UndefinedBecomesJsonNull()
    {
        var clone = Invoke("CloneJson", default(JsonElement));

        Assert.Equal(JsonValueKind.Null, clone.ValueKind);
    }

    // ─── IsTerminal ─────────────────────────────────────────────────────

    private static bool IsTerminal(string status)
        => (bool)Method("IsTerminal").Invoke(null, new object?[] { status })!;

    [Theory]
    [InlineData(StepStatus.Completed)]
    [InlineData(StepStatus.Failed)]
    [InlineData(StepStatus.Skipped)]
    [InlineData(StepStatus.Cancelled)]
    public void IsTerminal_TrueForFinishedStates(string status)
        => Assert.True(IsTerminal(status));

    [Theory]
    [InlineData(StepStatus.Pending)]
    [InlineData(StepStatus.Running)]
    [InlineData("something_else")]
    public void IsTerminal_FalseForInFlightStates(string status)
        => Assert.False(IsTerminal(status));

    // ─── BuildDeviceContext ─────────────────────────────────────────────

    private static JsonElement BuildDeviceContext(Device device)
        => Invoke("BuildDeviceContext", device);

    private static Device NewDevice(string name = "rtr-1", string? properties = null) => new()
    {
        DeviceId = Guid.NewGuid(),
        DeviceName = name,
        IpAddress = "10.0.0.1",
        Platform = "ios",
        Vendor = "cisco",
        OsVersion = "15.2",
        Site = "madrid",
        Role = "edge",
        Status = "active",
        Properties = properties is null ? default : TestJson.Element(properties),
        IsActive = true,
    };

    [Fact]
    public void BuildDeviceContext_ExposesTheStableSnakeCaseShape()
    {
        var device = NewDevice();

        var ctx = BuildDeviceContext(device);

        Assert.Equal(device.DeviceId.ToString(), ctx.GetProperty("id").GetString());
        Assert.Equal("rtr-1", ctx.GetProperty("name").GetString());
        Assert.Equal("10.0.0.1", ctx.GetProperty("ip").GetString());
        Assert.Equal("ios", ctx.GetProperty("platform").GetString());
        Assert.Equal("cisco", ctx.GetProperty("vendor").GetString());
        Assert.Equal("15.2", ctx.GetProperty("os_version").GetString());
        Assert.Equal("madrid", ctx.GetProperty("site").GetString());
        Assert.Equal("edge", ctx.GetProperty("role").GetString());
        Assert.Equal("active", ctx.GetProperty("status").GetString());
    }

    // Secrets and implementation detail must never reach a template.
    [Theory]
    [InlineData("credential_id")]
    [InlineData("last_sync_at")]
    [InlineData("ssh_host_key_fingerprint")]
    public void BuildDeviceContext_NeverExposesSecretsOrInternals(string forbidden)
    {
        var ctx = BuildDeviceContext(NewDevice());

        Assert.False(ctx.TryGetProperty(forbidden, out _));
    }

    [Fact]
    public void BuildDeviceContext_PassesCustomPropertiesThroughVerbatim()
    {
        var ctx = BuildDeviceContext(NewDevice(properties: """{"rack":"A1"}"""));

        Assert.Equal("A1", ctx.GetProperty("properties").GetProperty("rack").GetString());
    }

    [Fact]
    public void BuildDeviceContext_AbsentPropertiesBecomeNull()
    {
        var ctx = BuildDeviceContext(NewDevice());

        Assert.Equal(JsonValueKind.Null, ctx.GetProperty("properties").ValueKind);
    }

    // ─── AggregatePerDeviceOutput ───────────────────────────────────────

    private static StepRunModel Step(
        Guid? deviceId, string status, string? output = null, string? error = null, DateTime? createdAt = null)
        => new()
        {
            StepRunId = Guid.NewGuid(),
            NodeId = "node-1",
            DeviceId = deviceId,
            Status = status,
            Error = error,
            OutputPayload = output is null ? default : TestJson.Element(output),
            CreatedAt = createdAt ?? DateTime.UtcNow,
            IsActive = true,
        };

    private static JsonElement Aggregate(
        List<StepRunModel> steps, Dictionary<Guid, string>? names = null)
        => Invoke("AggregatePerDeviceOutput",
            steps,
            (IReadOnlyDictionary<Guid, string>)(names ?? new Dictionary<Guid, string>()),
            null,
            "node-1");

    [Fact]
    public void Aggregate_CountsEachOutcome()
    {
        var d1 = Guid.NewGuid();
        var d2 = Guid.NewGuid();
        var d3 = Guid.NewGuid();
        var result = Aggregate(new()
        {
            Step(d1, StepStatus.Completed),
            Step(d2, StepStatus.Failed),
            Step(d3, StepStatus.Skipped),
        });

        Assert.Equal(1, result.GetProperty("success_count").GetInt32());
        Assert.Equal(1, result.GetProperty("failure_count").GetInt32());
        Assert.Equal(1, result.GetProperty("skipped_count").GetInt32());
        Assert.Equal(3, result.GetProperty("total").GetInt32());
    }

    [Fact]
    public void Aggregate_ListsSuccessfulAndFailedDeviceNames()
    {
        var ok = Guid.NewGuid();
        var bad = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [ok] = "rtr-1", [bad] = "rtr-2" };

        var result = Aggregate(new() { Step(ok, StepStatus.Completed), Step(bad, StepStatus.Failed) }, names);

        Assert.Equal("rtr-1", Assert.Single(result.GetProperty("successful_devices").EnumerateArray()).GetString());
        Assert.Equal("rtr-2", Assert.Single(result.GetProperty("failed_devices").EnumerateArray()).GetString());
    }

    // `devices_by_name` is what templates index by hostname.
    [Fact]
    public void Aggregate_IndexesDevicesByHostname()
    {
        var id = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [id] = "rtr-1" };

        var result = Aggregate(new() { Step(id, StepStatus.Completed, """{"stdout":"IOS"}""") }, names);

        var entry = result.GetProperty("devices_by_name").GetProperty("rtr-1");
        Assert.Equal("IOS", entry.GetProperty("output").GetProperty("stdout").GetString());
        Assert.Equal(id.ToString(), entry.GetProperty("device_id").GetString());
    }

    // Every device entry carries the four name aliases so templates can use
    // whichever reads best.
    [Fact]
    public void Aggregate_EntryCarriesAllNameAliases()
    {
        var id = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [id] = "rtr-1" };

        var entry = Aggregate(new() { Step(id, StepStatus.Completed) }, names)
            .GetProperty("devices")[0];

        Assert.Equal("rtr-1", entry.GetProperty("device").GetString());
        Assert.Equal("rtr-1", entry.GetProperty("device_name").GetString());
        Assert.Equal("rtr-1", entry.GetProperty("hostname").GetString());
    }

    // The handler's output keys are spread onto the entry so
    // `devices[0].stdout` works without reaching through `.output`.
    [Fact]
    public void Aggregate_SpreadsOutputKeysOntoTheEntry()
    {
        var id = Guid.NewGuid();

        var entry = Aggregate(new() { Step(id, StepStatus.Completed, """{"stdout":"IOS","rc":0}""") })
            .GetProperty("devices")[0];

        Assert.Equal("IOS", entry.GetProperty("stdout").GetString());
        Assert.Equal(0, entry.GetProperty("rc").GetInt32());
        // ...and the raw output is still there verbatim.
        Assert.Equal("IOS", entry.GetProperty("output").GetProperty("stdout").GetString());
    }

    // A handler output key that collides with a reserved alias must NOT
    // overwrite it — the real value stays reachable under `.output`.
    [Theory]
    [InlineData("device_id")]
    [InlineData("device")]
    [InlineData("device_name")]
    [InlineData("hostname")]
    [InlineData("status")]
    [InlineData("error")]
    [InlineData("output")]
    public void Aggregate_ReservedKeysAreNotOverwrittenBySpread(string reserved)
    {
        var id = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [id] = "rtr-1" };
        var output = $$"""{"{{reserved}}":"HIJACKED"}""";

        var entry = Aggregate(new() { Step(id, StepStatus.Completed, output) }, names)
            .GetProperty("devices")[0];

        Assert.NotEqual("HIJACKED", entry.GetProperty(reserved).ToString());
        Assert.Equal("HIJACKED", entry.GetProperty("output").GetProperty(reserved).GetString());
    }

    [Fact]
    public void Aggregate_FirstSuccessPointsAtTheFirstCompletedDevice()
    {
        var bad = Guid.NewGuid();
        var ok = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [bad] = "rtr-bad", [ok] = "rtr-ok" };

        var result = Aggregate(new()
        {
            Step(bad, StepStatus.Failed, createdAt: DateTime.UtcNow.AddSeconds(-2)),
            Step(ok, StepStatus.Completed, """{"stdout":"yes"}""", createdAt: DateTime.UtcNow.AddSeconds(-1)),
        }, names);

        Assert.Equal("rtr-ok", result.GetProperty("first_success").GetProperty("device").GetString());
    }

    [Fact]
    public void Aggregate_NoSuccessesMeansFirstSuccessIsNull()
    {
        var result = Aggregate(new() { Step(Guid.NewGuid(), StepStatus.Failed) });

        Assert.Equal(JsonValueKind.Null, result.GetProperty("first_success").ValueKind);
    }

    [Fact]
    public void Aggregate_EmptyInputProducesAWellFormedEnvelope()
    {
        var result = Aggregate(new List<StepRunModel>());

        Assert.Empty(result.GetProperty("devices").EnumerateArray());
        Assert.Empty(result.GetProperty("devices_by_name").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("first_success").ValueKind);
        Assert.Equal(0, result.GetProperty("total").GetInt32());
    }

    // Duplicate step_runs for the same device (an orchestrator double-claim)
    // collapse to the latest, so `devices_by_name['rtr-1']` stays indexable by
    // the bare hostname instead of gaining an `rtr-1#2` sibling.
    [Fact]
    public void Aggregate_DuplicateStepRunsForOneDeviceCollapseToTheLatest()
    {
        var id = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [id] = "rtr-1" };

        var result = Aggregate(new()
        {
            Step(id, StepStatus.Failed, """{"attempt":1}""", createdAt: DateTime.UtcNow.AddSeconds(-5)),
            Step(id, StepStatus.Completed, """{"attempt":2}""", createdAt: DateTime.UtcNow),
        }, names);

        Assert.Equal(1, result.GetProperty("total").GetInt32());
        Assert.Single(result.GetProperty("devices").EnumerateArray());
        var entry = result.GetProperty("devices_by_name").GetProperty("rtr-1");
        Assert.Equal(2, entry.GetProperty("output").GetProperty("attempt").GetInt32());
        Assert.False(result.GetProperty("devices_by_name").TryGetProperty("rtr-1#2", out _));
    }

    // Two DIFFERENT devices that happen to share a hostname still both appear,
    // the second suffixed.
    [Fact]
    public void Aggregate_DistinctDevicesSharingAHostnameAreSuffixed()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [a] = "dup", [b] = "dup" };

        var byName = Aggregate(new() { Step(a, StepStatus.Completed), Step(b, StepStatus.Completed) }, names)
            .GetProperty("devices_by_name");

        Assert.True(byName.TryGetProperty("dup", out _));
        Assert.True(byName.TryGetProperty("dup#2", out _));
    }

    // A step with no device id (non-fan-out) is kept, not dropped.
    [Fact]
    public void Aggregate_StepWithoutADeviceIsKept()
    {
        var result = Aggregate(new() { Step(null, StepStatus.Completed, """{"ok":true}""") });

        Assert.Equal(1, result.GetProperty("total").GetInt32());
        var entry = result.GetProperty("devices")[0];
        Assert.Equal(string.Empty, entry.GetProperty("device_id").GetString());
        Assert.True(entry.GetProperty("output").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Aggregate_UnnamedDeviceFallsBackToItsGuidThenToUnnamedKey()
    {
        var id = Guid.NewGuid();

        var result = Aggregate(new() { Step(id, StepStatus.Completed) });

        // No name in the map → the guid is used as the display name.
        Assert.Equal(id.ToString(), result.GetProperty("devices")[0].GetProperty("device").GetString());
        Assert.True(result.GetProperty("devices_by_name").TryGetProperty(id.ToString(), out _));
    }

    [Fact]
    public void Aggregate_MissingOutputBecomesJsonNull()
    {
        var result = Aggregate(new() { Step(Guid.NewGuid(), StepStatus.Completed) });

        Assert.Equal(JsonValueKind.Null, result.GetProperty("devices")[0].GetProperty("output").ValueKind);
    }

    [Fact]
    public void Aggregate_ErrorIsSurfacedPerDevice()
    {
        var id = Guid.NewGuid();

        var result = Aggregate(new() { Step(id, StepStatus.Failed, error: "auth failed") });

        Assert.Equal("auth failed", result.GetProperty("devices")[0].GetProperty("error").GetString());
    }

    // ─── ScopeOutputsToDevice / TryExtractDeviceOutput ──────────────────

    private static IReadOnlyDictionary<string, StepResult> ScopeToDevice(
        Dictionary<string, StepResult> outputs, Guid deviceId)
        => (IReadOnlyDictionary<string, StepResult>)Method("ScopeOutputsToDevice")
            .Invoke(null, new object?[] { (IReadOnlyDictionary<string, StepResult>)outputs, deviceId })!;

    // A per_device consumer sees ITS OWN slice of an upstream fan-out, not the
    // whole aggregate — otherwise every device would read device[0]'s output.
    [Fact]
    public void ScopeToDevice_UnwrapsTheMatchingDeviceSlice()
    {
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var aggregate = TestJson.Element(
            "{\"devices\":["
            + "{\"device_id\":\"" + other + "\",\"output\":{\"stdout\":\"theirs\"}},"
            + "{\"device_id\":\"" + mine + "\",\"output\":{\"stdout\":\"mine\"}}"
            + "]}");
        var outputs = new Dictionary<string, StepResult> { ["upstream"] = new(aggregate) };

        var scoped = ScopeToDevice(outputs, mine);

        Assert.Equal("mine", scoped["upstream"].Output.GetProperty("stdout").GetString());
    }

    // Device-id matching is case-insensitive because guid casing varies by
    // producer.
    [Fact]
    public void ScopeToDevice_MatchesTheDeviceIdCaseInsensitively()
    {
        var id = Guid.NewGuid();
        var aggregate = TestJson.Element(
            "{\"devices\":[{\"device_id\":\"" + id.ToString().ToUpperInvariant()
            + "\",\"output\":{\"v\":1}}]}");
        var outputs = new Dictionary<string, StepResult> { ["upstream"] = new(aggregate) };

        var scoped = ScopeToDevice(outputs, id);

        Assert.Equal(1, scoped["upstream"].Output.GetProperty("v").GetInt32());
    }

    // A non-aggregate upstream (a `once` step) passes through untouched.
    [Theory]
    [InlineData("""{"plain":"output"}""")]
    [InlineData("""{"devices":"not-an-array"}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void ScopeToDevice_NonAggregateOutputsPassThroughUnchanged(string json)
    {
        var outputs = new Dictionary<string, StepResult> { ["upstream"] = new(TestJson.Element(json)) };

        var scoped = ScopeToDevice(outputs, Guid.NewGuid());

        Assert.Equal(TestJson.Element(json).GetRawText(), scoped["upstream"].Output.GetRawText());
    }

    // CONTRACT CHANGE (workflow.v1 SPEC §6): a per_device consumer reading a
    // fan-out producer that has NO entry for its device resolves to
    // *unresolved*. The producer is therefore dropped from the scoped map —
    // it used to fall back to the whole envelope, which let this device read
    // `{{ steps.up.output.total }}` (or `devices[0].output`) off data that
    // belongs to the OTHER devices.
    [Fact]
    public void ScopeToDevice_DeviceNotInTheAggregateIsOmitted()
    {
        var aggregate = TestJson.Element(
            "{\"devices\":[{\"device_id\":\"" + Guid.NewGuid()
            + "\",\"output\":{\"stdout\":\"theirs\"}}],\"total\":1}");
        var outputs = new Dictionary<string, StepResult> { ["upstream"] = new(aggregate) };

        var scoped = ScopeToDevice(outputs, Guid.NewGuid());

        Assert.False(scoped.ContainsKey("upstream"));
    }

    // The omission is per-producer: a sibling step this device DOES appear in
    // still resolves normally, and a once-shaped output is untouched.
    [Fact]
    public void ScopeToDevice_OmissionIsScopedToTheProducerThatLacksTheDevice()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var outputs = new Dictionary<string, StepResult>
        {
            ["has-me"] = new(TestJson.Element(
                "{\"devices\":[{\"device_id\":\"" + mine + "\",\"output\":{\"v\":1}}]}")),
            ["lacks-me"] = new(TestJson.Element(
                "{\"devices\":[{\"device_id\":\"" + theirs + "\",\"output\":{\"v\":2}}]}")),
            ["once-step"] = new(TestJson.Element("{\"plain\":\"output\"}")),
        };

        var scoped = ScopeToDevice(outputs, mine);

        Assert.Equal(1, scoped["has-me"].Output.GetProperty("v").GetInt32());
        Assert.False(scoped.ContainsKey("lacks-me"));
        Assert.Equal("output", scoped["once-step"].Output.GetProperty("plain").GetString());
    }

    // An entry present but with no `output` key is still "no entry for me":
    // there is nothing to scope to, so the producer is omitted rather than
    // handed over as the aggregate.
    [Fact]
    public void ScopeToDevice_EntryWithoutAnOutputKeyIsOmitted()
    {
        var mine = Guid.NewGuid();
        var aggregate = TestJson.Element(
            "{\"devices\":[{\"device_id\":\"" + mine + "\",\"error\":\"boom\"}]}");
        var outputs = new Dictionary<string, StepResult> { ["upstream"] = new(aggregate) };

        var scoped = ScopeToDevice(outputs, mine);

        Assert.False(scoped.ContainsKey("upstream"));
    }

    // "Is a fan-out envelope" is not the same as "has a devices key". An
    // ordinary output whose `devices` array carries no string `device_id`
    // (an inventory listing, say) is NOT an envelope and must reach the
    // consumer whole — omitting it would break once-shaped outputs that
    // merely share a key name.
    [Theory]
    [InlineData("{\"devices\":[]}")]
    [InlineData("{\"devices\":[{\"name\":\"rtr-1\"},{\"name\":\"rtr-2\"}]}")]
    [InlineData("{\"devices\":[\"rtr-1\",\"rtr-2\"]}")]
    [InlineData("{\"devices\":[{\"device_id\":42}]}")]
    public void ScopeToDevice_ObjectWithADevicesKeyThatIsNotAnEnvelopePassesThrough(string json)
    {
        var outputs = new Dictionary<string, StepResult> { ["upstream"] = new(TestJson.Element(json)) };

        var scoped = ScopeToDevice(outputs, Guid.NewGuid());

        Assert.Equal(TestJson.Element(json).GetRawText(), scoped["upstream"].Output.GetRawText());
    }

    // A producer omitted by ScopeOutputsToDevice does not silently vanish:
    // the reference stays literal and the residual scan — the thing that
    // fails the step — still names it, so the operator sees which reference
    // had no data for this device.
    [Fact]
    public void ScopeToDevice_OmittedProducerLeavesANamedUnresolvedTemplate()
    {
        var aggregate = TestJson.Element(
            "{\"devices\":[{\"device_id\":\"" + Guid.NewGuid()
            + "\",\"output\":{\"stdout\":\"theirs\"}}],\"total\":1}");
        var outputs = new Dictionary<string, StepResult> { ["show"] = new(aggregate) };

        var scoped = ScopeToDevice(outputs, Guid.NewGuid());
        var resolver = new VariableResolver(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VariableResolver>.Instance);
        var resolved = resolver.Resolve(
            TestJson.Element("{\"body\":\"{{ steps.show.output.total }}\"}"), scoped);

        var unresolved = VariableResolver.FindUnresolvedTemplates(resolved);

        Assert.Contains(unresolved, u => u.TemplateText.Contains("steps.show.output.total"));
    }
}
