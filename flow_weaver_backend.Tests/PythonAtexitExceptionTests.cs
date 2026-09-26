using flow_weaver_backend.Services.Worker.Handlers;

namespace flow_weaver_backend.Tests;

// CPython reports an exception escaping an atexit callback as "ignored":
// traceback on stderr, exit status 0. flowweaver_runtime auto-invokes
// run(ctx) from exactly such a callback, so a raising snippet used to come
// back as exit=0 with empty stdout and was recorded as a successful step
// with output {"raw": ""}. The runtime now exits non-zero itself; this is
// the handler-side safety net for when that path is bypassed.
public class PythonAtexitExceptionTests
{
    private const string IgnoredReport =
        "Exception ignored in atexit callback <function _auto_invoke_run at 0x7f>:\n"
        + "Traceback (most recent call last):\n"
        + "  File \"/usr/local/lib/flowweaver_runtime.py\", line 194, in _auto_invoke_run\n"
        + "    run_fn(None)\n"
        + "  File \"/tmp/fw-abc.py\", line 6, in run\n"
        + "    nb_devices = inp['steps']['fetch-netbox-devices']['output']['data']['results']\n"
        + "KeyError: 'results'\n";

    [Fact]
    public void Exit_zero_with_empty_stdout_and_ignored_report_is_a_failure()
    {
        Assert.True(PythonHandler.IsIgnoredAtexitException(0, "", IgnoredReport));
        Assert.True(PythonHandler.IsIgnoredAtexitException(0, "  \n", IgnoredReport));
    }

    [Fact]
    public void Script_that_produced_output_is_not_failed_by_a_noisy_atexit_hook()
    {
        Assert.False(PythonHandler.IsIgnoredAtexitException(0, "{\"ok\":true}", IgnoredReport));
    }

    [Fact]
    public void Non_zero_exit_is_left_to_the_regular_failure_path()
    {
        Assert.False(PythonHandler.IsIgnoredAtexitException(1, "", IgnoredReport));
    }

    [Fact]
    public void Ordinary_stderr_noise_does_not_match()
    {
        Assert.False(PythonHandler.IsIgnoredAtexitException(0, "", ""));
        Assert.False(PythonHandler.IsIgnoredAtexitException(0, "", null));
        Assert.False(PythonHandler.IsIgnoredAtexitException(0, "", "[INFO] processed 12 devices\n"));
        // A traceback the script printed and recovered from is not the atexit path.
        Assert.False(PythonHandler.IsIgnoredAtexitException(0, "", "Traceback (most recent call last):\nKeyError: 'x'\n"));
    }
}
