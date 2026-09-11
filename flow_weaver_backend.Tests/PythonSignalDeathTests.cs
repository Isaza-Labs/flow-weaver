using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Worker.Handlers;

namespace flow_weaver_backend.Tests;

// A sandboxed script killed by a signal used to surface as the bare string
// "exit code 137:" with empty stdout and stderr — the script produced nothing
// because SIGKILL is uncatchable and flowweaver_runtime flushes its output from
// an `atexit` hook that therefore never runs. That message named neither the
// signal nor anything to check, so the same failure recurred across runs
// without ever being diagnosed.
//
// Three of the signals reachable here are self-inflicted by the handler's own
// prlimit wrapper (SIGXCPU from --cpu, SIGXFSZ from --fsize, and SIGSEGV when
// --as is too low for the interpreter). Those are fixed by changing config, so
// the message has to tell them apart from SIGKILL, which is not ours.
public class PythonSignalDeathTests
{
    [Theory]
    [InlineData(137, 9)]    // SIGKILL — the reported case
    [InlineData(139, 11)]   // SIGSEGV
    [InlineData(152, 24)]   // SIGXCPU
    [InlineData(153, 25)]   // SIGXFSZ
    [InlineData(143, 15)]   // SIGTERM
    public void Exit_codes_above_128_decode_to_their_signal(int exitCode, int expectedSignal)
    {
        Assert.True(PythonHandler.IsSignalDeath(exitCode, out var signal));
        Assert.Equal(expectedSignal, signal);
    }

    [Theory]
    [InlineData(0)]     // success
    [InlineData(1)]     // ordinary Python exception
    [InlineData(2)]
    [InlineData(128)]   // boundary: not a signal
    [InlineData(193)]   // 128+65 — beyond any real signal, so a literal code
    [InlineData(200)]   // a script's own sys.exit(200)
    public void Ordinary_exit_codes_are_not_signal_deaths(int exitCode)
    {
        Assert.False(PythonHandler.IsSignalDeath(exitCode, out _));
    }

    [Fact]
    public void Sigkill_with_an_oom_verdict_blames_memory_and_names_the_knobs()
    {
        var msg = PythonHandler.SignalDeathError(
            signal: 9, elapsed: TimeSpan.FromSeconds(55.9), timeoutSec: 60,
            oomKilled: true, oomCounterReadable: true);

        Assert.Contains("OOM-killed", msg);
        Assert.Contains("Python:MaxConcurrentSandboxes", msg);
    }

    // The reported failure: SIGKILL with the OOM counter flat. The message must
    // actively rule FlowWeaver out rather than leave the operator guessing —
    // that ambiguity is what made this recur.
    [Fact]
    public void Sigkill_without_an_oom_verdict_points_outside_the_container()
    {
        var msg = PythonHandler.SignalDeathError(
            signal: 9, elapsed: TimeSpan.FromSeconds(55.9), timeoutSec: 60,
            oomKilled: false, oomCounterReadable: true);

        Assert.Contains("did NOT come from FlowWeaver", msg);
        Assert.Contains("did NOT increase", msg);
        Assert.Contains("dmesg", msg);
        Assert.Contains("55.9s", msg);
        Assert.Contains("60s", msg);
        // Must not blame memory when the kernel says memory was fine.
        Assert.DoesNotContain("OOM-killed", msg);
    }

    // When the counter is unreadable (cgroup not mounted, older kernel) the
    // message must say so rather than silently implying memory was ruled out.
    [Fact]
    public void Sigkill_with_an_unreadable_counter_says_oom_is_still_possible()
    {
        var msg = PythonHandler.SignalDeathError(
            signal: 9, elapsed: TimeSpan.FromSeconds(10), timeoutSec: 60,
            oomKilled: false, oomCounterReadable: false);

        Assert.Contains("could not be read", msg);
        Assert.DoesNotContain("did NOT increase", msg);
    }

    [Fact]
    public void Sigxcpu_blames_a_busy_loop_not_a_slow_call()
    {
        // The distinction matters: a script that waits on I/O or sleeps uses
        // almost no CPU, so hitting the CPU cap means spinning.
        var msg = PythonHandler.SignalDeathError(
            signal: 24, elapsed: TimeSpan.FromSeconds(59), timeoutSec: 60,
            oomKilled: false, oomCounterReadable: true);

        Assert.Contains("CPU", msg);
        Assert.Contains("time.sleep()", msg);
        Assert.Contains("timeout_seconds", msg);
    }

    [Fact]
    public void Sigxfsz_points_at_set_output_instead_of_files()
    {
        var msg = PythonHandler.SignalDeathError(
            signal: 25, elapsed: TimeSpan.FromSeconds(3), timeoutSec: 60,
            oomKilled: false, oomCounterReadable: true);

        Assert.Contains("10 MiB", msg);
        Assert.Contains("set_output()", msg);
    }

    [Fact]
    public void Sigsegv_names_the_address_space_cap()
    {
        var msg = PythonHandler.SignalDeathError(
            signal: 11, elapsed: TimeSpan.FromSeconds(1), timeoutSec: 60,
            oomKilled: false, oomCounterReadable: true);

        Assert.Contains("Python:MaxMemoryMb", msg);
    }

    [Fact]
    public void An_unmapped_signal_still_produces_an_actionable_message()
    {
        var msg = PythonHandler.SignalDeathError(
            signal: 7, elapsed: TimeSpan.FromSeconds(2), timeoutSec: 60,
            oomKilled: false, oomCounterReadable: true);

        Assert.Contains("signal 7", msg);
        Assert.Contains("no output", msg);
    }

    // Best-effort by contract: on a machine with no cgroup memory controller
    // (every dev box running these tests on Windows/macOS) this must return
    // null rather than throw, because a failed reading must never affect
    // whether a step succeeds.
    [Fact]
    public void Reading_the_oom_counter_never_throws()
    {
        var ex = Record.Exception(() => CgroupOom.ReadKillCount());
        Assert.Null(ex);
    }
}
