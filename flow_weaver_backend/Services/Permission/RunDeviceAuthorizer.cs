using System.Text.Json;
using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;

namespace flow_weaver_backend.Services.Permission;

// Granular-mode check for the DEVICE dimension of a manual run.
//
// WorkflowController.Run already honours the environment and resource
// conditions of `workflow.run`, but nothing supplied a device context, so:
//   * a grant conditioned on device_role / device_pool / device_ids could never
//     match (fail-closed, but unusable), and
//   * `device.exec.read` / `device.exec.write` were checked nowhere — a user
//     holding `workflow.run` without them could still send commands to devices.
//
// This runs at enqueue, per resolved target device, with that device's id,
// role and pool names:
//   1. `workflow.run` must hold for every device;
//   2. if the graph touches devices, `device.exec.read` or `device.exec.write`
//      (see RequiredExecCapability) must hold for every device.
//
// System runs (scheduler, webhooks, git) enqueue through the executor directly
// and are not a user's interactive action, same as the existing run check.
public sealed class RunDeviceAuthorizer
{
    public const string ExecRead = "device.exec.read";
    public const string ExecWrite = "device.exec.write";

    // Handler types that send something to a device. `ping` only probes
    // reachability and needs nothing beyond workflow.run.
    private static readonly HashSet<string> WriteTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ansible_playbook", "netconf",
    };
    private static readonly HashSet<string> ReadTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "snmp_v3",
    };

    // Leading verbs that only read state on the network OSes the catalogue
    // covers. Anything else — or anything we can't see (templates) — is a write.
    private static readonly string[] ReadVerbs =
    {
        "show", "display", "get", "ping", "traceroute", "tracert", "info",
    };

    // A read verb can still write through an output redirect
    // (`show run | redirect flash:x`, `display ... | save`). Matched as the
    // verb that opens a pipe segment, so `show log | include save-config`
    // stays a read.
    private static readonly HashSet<string> RedirectVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "redirect", "save", "tee", "append", "copy", "write",
    };

    private readonly AppDbContext _db;
    private readonly IEffectivePermissions _effective;

    public RunDeviceAuthorizer(AppDbContext db, IEffectivePermissions effective)
    {
        _db = db;
        _effective = effective;
    }

    public sealed record Denial(string Capability, Guid? DeviceId, string? DeviceName);

    // Returns null when allowed, or the first denial found.
    public async Task<Denial?> AuthorizeAsync(
        Guid workflowId, string environment, JsonElement nodes, JsonElement runInput,
        IReadOnlyCollection<Guid> targetDevices, IReadOnlyCollection<Guid> targetPools,
        CancellationToken ct)
    {
        var exec = await RequiredExecCapabilityAsync(nodes, runInput, ct);
        var devices = await ResolveDevicesAsync(targetDevices, targetPools, environment, ct);

        if (devices.Count == 0)
        {
            // No device context (no targets, or none that this environment would
            // run). A device-conditioned grant cannot match here, which is the
            // intended fail-closed behaviour for an unscoped call.
            var unscoped = new PermissionContext(
                Environment: environment, ResourceType: "workflow", ResourceId: workflowId);
            if (!await _effective.HasAsync("workflow.run", unscoped, ct))
                return new Denial("workflow.run", null, null);
            if (exec is not null && !await _effective.HasAsync(exec, unscoped, ct))
                return new Denial(exec, null, null);
            return null;
        }

        foreach (var d in devices)
        {
            var ctx = new PermissionContext(
                Environment: environment,
                DeviceRoles: string.IsNullOrWhiteSpace(d.Role) ? null : new[] { d.Role },
                DevicePoolNames: d.PoolNames.Count == 0 ? null : d.PoolNames,
                DeviceIds: new[] { d.DeviceId },
                ResourceType: "workflow",
                ResourceId: workflowId);

            if (!await _effective.HasAsync("workflow.run", ctx, ct))
                return new Denial("workflow.run", d.DeviceId, d.Name);
            if (exec is not null && !await _effective.HasAsync(exec, ctx, ct))
                return new Denial(exec, d.DeviceId, d.Name);
        }
        return null;
    }

    // null = the graph never talks to a device; otherwise the capability the
    // most demanding device-touching node needs.
    //
    // The run input is merged into every step's payload (a key the node does not
    // set comes from the input), so an input carrying `command` / `commands` can
    // change what an ssh step sends: any ssh node then counts as a write.
    public async Task<string?> RequiredExecCapabilityAsync(
        JsonElement nodes, JsonElement runInput, CancellationToken ct,
        int depth = 0, HashSet<Guid>? visited = null)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return null;
        visited ??= new HashSet<Guid>();
        string? required = null;
        // The merge gives the node's own config precedence, so a run input only
        // decides the commands for a key the node leaves unset. `commands` is the
        // canonical key and beats `command` in the handler, so an input that
        // carries `commands` can replace a node that only sets `command`.
        bool InputOverrides(JsonElement overrides)
        {
            if (runInput.ValueKind != JsonValueKind.Object) return false;
            var inputHasCommands = runInput.TryGetProperty("commands", out _);
            var inputHasCommand = runInput.TryGetProperty("command", out _);
            if (!inputHasCommands && !inputHasCommand) return false;
            var nodeHasCommands = overrides.ValueKind == JsonValueKind.Object
                                  && overrides.TryGetProperty("commands", out _);
            var nodeHasCommand = overrides.ValueKind == JsonValueKind.Object
                                 && overrides.TryGetProperty("command", out _);
            if (inputHasCommands && !nodeHasCommands) return true;   // canonical key wins
            if (inputHasCommand && !nodeHasCommand && !nodeHasCommands) return true;
            return false;
        }

        var bySnippet = new List<(Guid snippetId, JsonElement overrides)>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("snippet_id", out var sid)
                || sid.ValueKind != JsonValueKind.String
                || !Guid.TryParse(sid.GetString(), out var id))
                continue;
            var overrides = node.TryGetProperty("config_overrides", out var co)
                            && co.ValueKind == JsonValueKind.Object ? co : default;
            bySnippet.Add((id, overrides));
        }
        if (bySnippet.Count == 0 && SubflowWorkflowIds(nodes).Count == 0) return null;

        // A subflow node's own device work lives in the child workflow, which the
        // executor enqueues directly — this is the only place it can be checked.
        var subflowIds = SubflowWorkflowIds(nodes);
        if (subflowIds.Count > 0 && depth < MaxSubflowDepth)
        {
            var children = await _db.Workflows
                .AsNoTracking()
                .Where(w => subflowIds.Contains(w.WorkflowId) && w.IsActive)
                .Select(w => new { w.WorkflowId, w.Nodes })
                .ToListAsync(ct);
            foreach (var child in children)
            {
                if (!visited.Add(child.WorkflowId)) continue;   // cycle guard
                var childNeed = await RequiredExecCapabilityAsync(child.Nodes, runInput, ct, depth + 1, visited);
                if (childNeed == ExecWrite) return ExecWrite;
                required ??= childNeed;
            }
        }

        var ids = bySnippet.Select(n => n.snippetId).Distinct().ToList();
        var snippets = await _db.Snippets
            .AsNoTracking()
            .Where(s => ids.Contains(s.SnippetId))
            .Select(s => new { s.SnippetId, s.Type, s.NetworkEnabled })
            .ToDictionaryAsync(s => s.SnippetId, ct);

        foreach (var (snippetId, overrides) in bySnippet)
        {
            if (!snippets.TryGetValue(snippetId, out var s)) continue;
            string? need = null;
            if (WriteTypes.Contains(s.Type)) need = ExecWrite;
            else if (ReadTypes.Contains(s.Type)) need = ExecRead;
            else if (string.Equals(s.Type, "ssh", StringComparison.OrdinalIgnoreCase))
                need = !InputOverrides(overrides) && SshIsReadOnly(overrides) ? ExecRead : ExecWrite;
            else if (s.NetworkEnabled) need = ExecWrite;   // interactive SSH from Python

            if (need == ExecWrite) return ExecWrite;
            required ??= need;
        }
        return required;
    }

    private const int MaxSubflowDepth = 5;

    // Workflow ids referenced by `subflow` nodes. The node stores the child's id
    // under `subflow_workflow_id` (the editor also keeps `subflow_name` for
    // display); anything that isn't a GUID is resolved by the executor and
    // cannot be pre-checked here.
    private static List<Guid> SubflowWorkflowIds(JsonElement nodes)
    {
        var ids = new List<Guid>();
        if (nodes.ValueKind != JsonValueKind.Array) return ids;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            if (!node.TryGetProperty("snippet_id", out var sid)
                || sid.ValueKind != JsonValueKind.String
                || !string.Equals(sid.GetString(), "subflow", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!node.TryGetProperty("config_overrides", out var co) || co.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var key in new[] { "subflow_workflow_id", "workflow_id", "subflow_id" })
                if (co.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    && Guid.TryParse(v.GetString(), out var id))
                {
                    ids.Add(id);
                    break;
                }
        }
        return ids;
    }

    private static bool SshIsReadOnly(JsonElement overrides)
    {
        if (overrides.ValueKind != JsonValueKind.Object) return false;
        var commands = new List<string>();
        if (overrides.TryGetProperty("command", out var single))
        {
            if (single.ValueKind != JsonValueKind.String) return false;
            commands.Add(single.GetString() ?? string.Empty);
        }
        if (overrides.TryGetProperty("commands", out var batch))
        {
            if (batch.ValueKind != JsonValueKind.Array) return false;
            foreach (var c in batch.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.String) return false;
                commands.Add(c.GetString() ?? string.Empty);
            }
        }
        if (commands.Count == 0) return false;
        return commands.All(IsReadOnlyCommand);
    }

    internal static bool IsReadOnlyCommand(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return false;
        if (text.Contains("{{", StringComparison.Ordinal)) return false;   // unknown until run time
        if (text.Contains('\n') || text.Contains(';')) return false;       // more than one command

        var lower = text.ToLowerInvariant();
        var firstWord = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (!ReadVerbs.Contains(firstWord)) return false;

        var segments = lower.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments.Skip(1))
        {
            var verb = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (verb is not null && RedirectVerbs.Contains(verb)) return false;
        }
        return !lower.Contains('>');
    }

    private sealed record TargetDevice(Guid DeviceId, string Name, string Role, IReadOnlyList<string> PoolNames);

    // One copy of the rule, shared with the executor's target resolution: a
    // device the run would drop is not the caller's to be denied on.
    private static bool AllowsEnvironment(string environment, bool draft, bool qa, bool production)
        => Engine.WorkflowExecutor.EnvironmentAllows(environment, draft, qa, production);

    private async Task<List<TargetDevice>> ResolveDevicesAsync(
        IReadOnlyCollection<Guid> targetDevices, IReadOnlyCollection<Guid> targetPools,
        string environment, CancellationToken ct)
    {
        if (targetDevices.Count == 0 && targetPools.Count == 0) return new();

        var ids = new HashSet<Guid>(targetDevices);
        if (targetPools.Count > 0)
        {
            var requested = await _db.DevicePools
                .AsNoTracking()
                .Where(p => targetPools.Contains(p.DevicePoolId) && p.IsActive)
                .ToListAsync(ct);
            foreach (var p in requested)
            {
                if (!AllowsEnvironment(environment, p.AllowDraft, p.AllowQa, p.AllowProduction)) continue;
                foreach (var m in p.StaticMembers) ids.Add(m);
            }
        }
        if (ids.Count == 0) return new();

        var devices = await _db.Devices
            .AsNoTracking()
            .Where(d => ids.Contains(d.DeviceId) && d.IsActive)
            .Select(d => new { d.DeviceId, d.DeviceName, d.Role, d.AllowDraft, d.AllowQa, d.AllowProduction })
            .ToListAsync(ct);
        devices = devices
            .Where(d => AllowsEnvironment(environment, d.AllowDraft, d.AllowQa, d.AllowProduction))
            .ToList();

        // Pool membership is what a device_pool condition names, so every active
        // pool that lists the device counts — not only the ones requested.
        var pools = await _db.DevicePools
            .AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => new { p.Name, p.StaticMembers })
            .ToListAsync(ct);

        return devices
            .Select(d => new TargetDevice(
                d.DeviceId,
                d.DeviceName,
                d.Role,
                pools.Where(p => p.StaticMembers.Contains(d.DeviceId))
                     .Select(p => p.Name)
                     .ToList()))
            .ToList();
    }
}
