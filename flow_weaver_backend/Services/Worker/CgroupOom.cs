namespace flow_weaver_backend.Services.Worker;

/// <summary>
/// Reads the kernel's OOM-kill counter for this container's cgroup.
/// </summary>
/// <remarks>
/// <para>
/// Exists to answer one question that is otherwise unanswerable after the
/// fact: when a sandboxed process dies of SIGKILL, was it the kernel's
/// out-of-memory killer, or did something else kill it?
/// </para>
/// <para>
/// Both look identical from the parent — exit 137, no stdout, no stderr,
/// because SIGKILL cannot be caught and `flowweaver_runtime` flushes its
/// output from an `atexit` hook that therefore never runs. Sampling the
/// counter around the run turns that ambiguity into a fact: if `oom_kill`
/// went up while our step was running, the kernel did it; if it did not,
/// the kill came from outside the container and no amount of tuning
/// `Python:MaxMemoryMb` will help.
/// </para>
/// <para>
/// Entirely best-effort: every failure path returns null, and a null reading
/// simply means the message omits the OOM verdict. This must never affect
/// whether a step succeeds.
/// </para>
/// </remarks>
public static class CgroupOom
{
    // cgroup v2 — the unified hierarchy. `memory.events` has a line
    // `oom_kill <n>` counting kills in this cgroup and its descendants.
    private const string V2Events = "/sys/fs/cgroup/memory.events";

    // cgroup v1 — `memory.oom_control` has `oom_kill <n>` on newer kernels.
    // Older v1 kernels only expose `oom_kill_disable` / `under_oom`, in which
    // case the parse below finds nothing and we return null.
    private const string V1OomControl = "/sys/fs/cgroup/memory/memory.oom_control";

    /// <summary>
    /// Current cumulative OOM-kill count for this cgroup, or null when the
    /// counter is unreadable (non-Linux, cgroup not mounted, older kernel).
    /// </summary>
    public static long? ReadKillCount()
    {
        foreach (var path in new[] { V2Events, V1OomControl })
        {
            var value = TryReadCounter(path);
            if (value is not null) return value;
        }
        return null;
    }

    private static long? TryReadCounter(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
            {
                // Format is `<key> <value>`, one pair per line.
                var space = line.IndexOf(' ');
                if (space <= 0) continue;
                if (!line.AsSpan(0, space).SequenceEqual("oom_kill")) continue;
                return long.TryParse(line.AsSpan(space + 1).Trim(), out var n) ? n : null;
            }
        }
        catch
        {
            // Unreadable for any reason (permissions, race with a cgroup
            // being torn down) — the caller treats null as "no verdict".
        }
        return null;
    }
}
