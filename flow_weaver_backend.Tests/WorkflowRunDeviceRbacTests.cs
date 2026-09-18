using System.Text.Json;
using flow_weaver_backend.Controllers;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Engine;
using flow_weaver_backend.Services.Permission;
using flow_weaver_backend.Services.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowModel = flow_weaver_backend.Models.Workflow;
using WorkflowRunModel = flow_weaver_backend.Models.WorkflowRun;

namespace flow_weaver_backend.Tests;

// The device dimension of a manual run in granular mode. Before this check,
// nothing supplied a device context: device-scoped workflow.run grants could
// never match, and device.exec.read / device.exec.write were enforced nowhere,
// so a user with workflow.run alone could send any command to any device.
public class WorkflowRunDeviceRbacTests
{
    private static readonly Guid User = new("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public RecordingExecutor Executor { get; } = new();

        public Guid SshSnippet { get; }
        public Guid PingSnippet { get; }
        public Guid AnsibleSnippet { get; }

        public Fixture()
        {
            SshSnippet = AddSnippet("ssh");
            PingSnippet = AddSnippet("ping");
            AnsibleSnippet = AddSnippet("ansible_playbook");
        }

        private Guid AddSnippet(string type)
        {
            var id = Guid.NewGuid();
            Db.Snippets.Add(new Snippet { SnippetId = id, Name = type, Type = type, IsActive = true });
            Db.SaveChanges();
            return id;
        }

        public Guid Device(string name, string role, bool allowDraft = true)
        {
            var id = Guid.NewGuid();
            Db.Devices.Add(new Device
            {
                DeviceId = id, DeviceName = name, Role = role, IpAddress = "192.0.2.1",
                AllowDraft = allowDraft, IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        public Guid Pool(string name, params Guid[] members)
        {
            var id = Guid.NewGuid();
            Db.DevicePools.Add(new DevicePool
            {
                DevicePoolId = id, Name = name, StaticMembers = members.ToList(), IsActive = true,
            });
            Db.SaveChanges();
            return id;
        }

        private bool _runRowSeeded;

        public WorkflowModel Workflow(string nodesJson)
        {
            var wf = new WorkflowModel
            {
                WorkflowId = Guid.NewGuid(), Name = "wf", Environment = "draft",
                Nodes = JsonDocument.Parse(nodesJson).RootElement.Clone(),
                Edges = JsonDocument.Parse("[]").RootElement.Clone(),
                IsActive = true,
            };
            Db.Workflows.Add(wf);
            // The controller re-reads the run row after enqueue; one row is
            // enough (the fake executor always returns the same id), and adding
            // it twice would collide on the key.
            if (!_runRowSeeded)
            {
                Db.WorkflowRuns.Add(new WorkflowRunModel
                {
                    WorkflowRunId = Executor.RunId, WorkflowId = wf.WorkflowId,
                    Status = "pending", Trigger = "manual", IsActive = true,
                });
                _runRowSeeded = true;
            }
            Db.SaveChanges();
            return wf;
        }

        public void Grant(string conditionsJson, params string[] capabilities)
        {
            Db.PermissionGrants.Add(new PermissionGrant
            {
                PermissionGrantId = Guid.NewGuid(), Name = "g-" + Guid.NewGuid().ToString("N")[..6],
                Enabled = true, IsActive = true,
                SubjectIds = new() { User },
                Capabilities = capabilities.ToList(),
                Conditions = JsonDocument.Parse(conditionsJson).RootElement.Clone(),
            });
            Db.SaveChanges();
        }

        public WorkflowController Controller(string mode = RbacModes.Granular)
        {
            var caller = new FakeUser { UserId = User, Roles = new[] { "operator" } };
            return new WorkflowController(
                service: null!, triggers: null!, executor: Executor, promotion: null!,
                caller: caller, db: Db, versions: null!, exportService: null!,
                bundleService: null!, bundleImporter: null!, snippets: null!,
                effective: new EffectivePermissions(caller, new PermissionGrantReader(Db)),
                settings: new StubSettings(mode),
                logger: NullLogger<WorkflowController>.Instance);
        }

        public void Dispose() => Db.Dispose();
    }

    private static string Node(Guid snippetId, string overrides = "{}")
        => "[{\"id\":\"n1\",\"snippet_id\":\"" + snippetId + "\",\"config_overrides\":" + overrides + "}]";

    private static async Task<int?> Run(Fixture f, WorkflowModel wf, RunWorkflowRequest request)
    {
        var result = await f.Controller().Run(wf.WorkflowId, request, CancellationToken.None);
        return Assert.IsType<ObjectResult>(result.Result).StatusCode;
    }

    // ─── device conditions on workflow.run ─────────────────────────────

    [Fact]
    public async Task ADeviceRoleScopedRunnerCanRunOnAMatchingDevice()
    {
        using var f = new Fixture();
        var access = f.Device("sw1", "access");
        var wf = f.Workflow(Node(f.PingSnippet));
        f.Grant("""{ "device_role": ["access"] }""", "workflow.run");

        var status = await Run(f, wf, new RunWorkflowRequest { TargetDevices = new() { access } });

        Assert.Equal(202, status);
        Assert.True(f.Executor.Called);
    }

    [Fact]
    public async Task ADeviceRoleScopedRunnerIsBlockedOnAnotherDevice()
    {
        using var f = new Fixture();
        var access = f.Device("sw1", "access");
        var core = f.Device("core1", "core");
        var wf = f.Workflow(Node(f.PingSnippet));
        f.Grant("""{ "device_role": ["access"] }""", "workflow.run");

        var status = await Run(f, wf, new RunWorkflowRequest { TargetDevices = new() { access, core } });

        Assert.Equal(403, status);
        Assert.False(f.Executor.Called);
    }

    [Fact]
    public async Task APoolScopedGrantMatchesTheMembersOfThatPool()
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var pool = f.Pool("edge", sw);
        var wf = f.Workflow(Node(f.PingSnippet));
        f.Grant("""{ "device_pool": ["edge"] }""", "workflow.run");

        var status = await Run(f, wf, new RunWorkflowRequest { TargetPools = new() { pool } });

        Assert.Equal(202, status);
    }

    // A device the environment filter drops is not run, so it is not checked.
    [Fact]
    public async Task ADeviceTheEnvironmentDropsIsNotChecked()
    {
        using var f = new Fixture();
        var access = f.Device("sw1", "access");
        var parked = f.Device("core1", "core", allowDraft: false);
        var wf = f.Workflow(Node(f.PingSnippet));
        f.Grant("""{ "device_role": ["access"] }""", "workflow.run");

        var status = await Run(f, wf, new RunWorkflowRequest { TargetDevices = new() { access, parked } });

        Assert.Equal(202, status);
    }

    // ─── device.exec.* ──────────────────────────────────────────────────

    [Fact]
    public async Task WorkflowRunAloneCannotSendCommandsToADevice()
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var wf = f.Workflow(Node(f.SshSnippet, """{ "command": "show version" }"""));
        f.Grant("{}", "workflow.run");

        var status = await Run(f, wf, new RunWorkflowRequest { TargetDevices = new() { sw } });

        Assert.Equal(403, status);
        Assert.False(f.Executor.Called);
    }

    [Fact]
    public async Task ReadOnlyCommandsNeedOnlyExecRead()
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var wf = f.Workflow(Node(f.SshSnippet, """{ "commands": ["show version", "display interface brief"] }"""));
        f.Grant("{}", "workflow.run", "device.exec.read");

        var status = await Run(f, wf, new RunWorkflowRequest { TargetDevices = new() { sw } });

        Assert.Equal(202, status);
    }

    [Theory]
    [InlineData("""{ "command": "configure terminal" }""")]
    [InlineData("""{ "command": "show running-config | redirect flash:backup" }""")]
    [InlineData("""{ "command": "{{ input.cmd }}" }""")]
    [InlineData("""{}""")]
    public async Task AnythingNotProvablyReadOnlyNeedsExecWrite(string overrides)
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var wf = f.Workflow(Node(f.SshSnippet, overrides));
        f.Grant("{}", "workflow.run", "device.exec.read");

        var status = await Run(f, wf, new RunWorkflowRequest { TargetDevices = new() { sw } });

        Assert.Equal(403, status);
    }

    // The run input is merged into the step payload, so it could replace a
    // read-only command list.
    [Fact]
    public async Task RunInputCarryingCommandsNeedsExecWrite()
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var wf = f.Workflow(Node(f.SshSnippet, """{ "command": "show version" }"""));
        f.Grant("{}", "workflow.run", "device.exec.read");

        var status = await Run(f, wf, new RunWorkflowRequest
        {
            TargetDevices = new() { sw },
            Input = JsonDocument.Parse("""{ "commands": ["reload"] }""").RootElement.Clone(),
        });

        Assert.Equal(403, status);
    }

    [Fact]
    public async Task AnAnsiblePlaybookNeedsExecWrite()
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var wf = f.Workflow(Node(f.AnsibleSnippet));
        f.Grant("{}", "workflow.run", "device.exec.read");
        Assert.Equal(403, await Run(f, wf, new RunWorkflowRequest { TargetDevices = new() { sw } }));
    }

    [Fact]
    public async Task ExecWriteScopedToAnotherRoleIsNotEnough()
    {
        using var f = new Fixture();
        var core = f.Device("core1", "core");
        var wf = f.Workflow(Node(f.SshSnippet, """{ "command": "configure terminal" }"""));
        f.Grant("{}", "workflow.run");
        f.Grant("""{ "device_role": ["access"] }""", "device.exec.write");

        Assert.Equal(403, await Run(f, wf, new RunWorkflowRequest { TargetDevices = new() { core } }));
    }

    [Fact]
    public async Task ExecWriteOnTheRightRoleAllowsTheRun()
    {
        using var f = new Fixture();
        var core = f.Device("core1", "core");
        var wf = f.Workflow(Node(f.SshSnippet, """{ "command": "configure terminal" }"""));
        f.Grant("{}", "workflow.run");
        f.Grant("""{ "device_role": ["core"] }""", "device.exec.write");

        Assert.Equal(202, await Run(f, wf, new RunWorkflowRequest { TargetDevices = new() { core } }));
    }

    // A ping only probes reachability.
    [Fact]
    public async Task APingNeedsNoExecCapability()
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var wf = f.Workflow(Node(f.PingSnippet));
        f.Grant("{}", "workflow.run");

        Assert.Equal(202, await Run(f, wf, new RunWorkflowRequest { TargetDevices = new() { sw } }));
    }

    [Fact]
    public async Task LegacyModeIsUnchanged()
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var wf = f.Workflow(Node(f.SshSnippet, """{ "command": "reload" }"""));

        var result = await f.Controller(RbacModes.Legacy).Run(
            wf.WorkflowId, new RunWorkflowRequest { TargetDevices = new() { sw } }, CancellationToken.None);

        Assert.Equal(202, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Theory]
    [InlineData("show version", true)]
    [InlineData("  display current-configuration", true)]
    [InlineData("show run | include hostname", true)]
    [InlineData("show log | include save-config", true)]
    [InlineData("show int | grep copper", true)]
    [InlineData("show run | redirect flash:x", false)]
    [InlineData("show run | tee flash:x", false)]
    [InlineData("show run > flash:x", false)]
    [InlineData("show version; reload", false)]
    [InlineData("write memory", false)]
    [InlineData("", false)]
    public void ReadOnlyClassification(string command, bool readOnly)
        => Assert.Equal(readOnly, RunDeviceAuthorizer.IsReadOnlyCommand(command));

    // ─── review follow-ups ──────────────────────────────────────────────

    // The node's own config wins the merge, so a run input that repeats a key
    // the node already sets changes nothing and must not force a write.
    [Fact]
    public async Task RunInputRepeatingTheNodesOwnKeyStaysARead()
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var wf = f.Workflow(Node(f.SshSnippet, """{ "command": "show version" }"""));
        f.Grant("{}", "workflow.run", "device.exec.read");

        var status = await Run(f, wf, new RunWorkflowRequest
        {
            TargetDevices = new() { sw },
            Input = JsonDocument.Parse("""{ "command": "reload" }""").RootElement.Clone(),
        });

        Assert.Equal(202, status);
    }

    // `commands` is canonical and beats a node that only sets `command`.
    [Fact]
    public async Task RunInputCommandsStillBeatsANodeThatOnlySetsCommand()
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var wf = f.Workflow(Node(f.SshSnippet, """{ "command": "show version" }"""));
        f.Grant("{}", "workflow.run", "device.exec.read");

        var status = await Run(f, wf, new RunWorkflowRequest
        {
            TargetDevices = new() { sw },
            Input = JsonDocument.Parse("""{ "commands": ["reload"] }""").RootElement.Clone(),
        });

        Assert.Equal(403, status);
    }

    // Device work inside a subflow child counts: the child run is created by the
    // executor and never passes through this check.
    [Fact]
    public async Task DeviceWorkInsideASubflowNeedsTheExecCapability()
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var child = f.Workflow(Node(f.SshSnippet, """{ "command": "configure terminal" }"""));
        var parent = f.Workflow(
            "[{\"id\":\"call\",\"snippet_id\":\"subflow\",\"config_overrides\":{\"subflow_workflow_id\":\""
            + child.WorkflowId + "\"}}]");
        f.Grant("{}", "workflow.run", "device.exec.read");

        var status = await Run(f, parent, new RunWorkflowRequest { TargetDevices = new() { sw } });

        Assert.Equal(403, status);
    }

    [Fact]
    public async Task AReadOnlySubflowNeedsOnlyExecRead()
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var child = f.Workflow(Node(f.SshSnippet, """{ "command": "show version" }"""));
        var parent = f.Workflow(
            "[{\"id\":\"call\",\"snippet_id\":\"subflow\",\"config_overrides\":{\"subflow_workflow_id\":\""
            + child.WorkflowId + "\"}}]");
        f.Grant("{}", "workflow.run", "device.exec.read");

        Assert.Equal(202, await Run(f, parent, new RunWorkflowRequest { TargetDevices = new() { sw } }));
    }

    // A subflow that points at itself must not spin the check forever.
    [Fact]
    public async Task ASelfReferencingSubflowDoesNotLoop()
    {
        using var f = new Fixture();
        var sw = f.Device("sw1", "access");
        var wf = f.Workflow("[]");
        var selfCall = TestJson.Element(
            "[{\"id\":\"call\",\"snippet_id\":\"subflow\",\"config_overrides\":{\"subflow_workflow_id\":\""
            + wf.WorkflowId + "\"}}]");
        wf.Nodes = selfCall;
        f.Db.SaveChanges();
        f.Grant("{}", "workflow.run");

        Assert.Equal(202, await Run(f, wf, new RunWorkflowRequest { TargetDevices = new() { sw } }));
    }

    internal sealed class RecordingExecutor : IWorkflowExecutor
    {
        public Guid RunId { get; } = Guid.NewGuid();
        public bool Called { get; private set; }

        public Task<Guid> EnqueueRunAsync(
            Guid userId, Guid workflowId,
            RunWorkflowRequest request, CancellationToken ct, string trigger = "manual")
        {
            Called = true;
            return Task.FromResult(RunId);
        }

        public Task ExecuteRunAsync(Guid workflowRunId, CancellationToken ct, Guid? jobId = null, string? workerId = null)
            => throw new NotImplementedException();
    }

    private sealed class StubSettings(string mode) : IAppSettingsService
    {
        public Task<AppSettings> GetAsync(CancellationToken ct = default)
            => Task.FromResult(new AppSettings { RbacMode = mode });
        public Task<AppSettings> UpdateAsync(AppSettings updated, CancellationToken ct = default)
            => Task.FromResult(updated);
    }
}
