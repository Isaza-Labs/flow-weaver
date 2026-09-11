using flow_weaver_backend.Services.Worker.Handlers;

namespace flow_weaver_backend.Tests;

// `bwrap: setting up uid map: Permission denied` is a DIFFERENT failure from
// "could not create a namespace", and conflating them cost real debugging time
// on a customer's Ubuntu 24.04 box: the namespace was created and only the
// write to /proc/self/uid_map was refused, which on Ubuntu 23.10+ means
// `kernel.apparmor_restrict_unprivileged_userns=1` — a fixed host policy.
//
// Two consequences the code has to get right:
//   * it is deterministic, so retrying it is pure delay;
//   * the generic advice names `kernel.unprivileged_userns_clone`, a
//     Debian-era knob that does not exist on those releases — following it
//     finds nothing and discredits the whole message.
public class SandboxUidMapDiagnosticTests
{
    private const string UidMapDenied = "bwrap: setting up uid map: Permission denied";

    [Fact]
    public void A_uid_map_denial_is_not_retried()
    {
        // Fixed host policy: four attempts with backoff produce the same answer,
        // slower.
        Assert.False(PythonHandler.IsTransientSandboxStartupFailure(1, "", UidMapDenied));
    }

    [Fact]
    public void A_uid_map_denial_is_still_recognised_as_a_setup_failure()
    {
        // Dropping it from the retry set must not drop it from the set that
        // earns the actionable message — otherwise it degrades to raw bwrap text.
        Assert.True(PythonHandler.IsSandboxSetupFailure(1, "", UidMapDenied));
    }

    [Fact]
    public void Namespace_exhaustion_is_still_retried()
    {
        // The genuinely contended case: a sibling releasing a namespace fixes it.
        Assert.True(PythonHandler.IsTransientSandboxStartupFailure(
            1, "", "bwrap: Creating new namespace failed: Resource temporarily unavailable"));
    }

    [Fact]
    public void The_uid_map_message_names_the_ubuntu_knob_and_not_the_obsolete_one()
    {
        var msg = PythonHandler.SandboxSetupError(UidMapDenied);

        Assert.Contains("apparmor_restrict_unprivileged_userns", msg);
        // The Debian-era knob must not appear here: on the releases that
        // produce this error it does not exist.
        Assert.DoesNotContain("unprivileged_userns_clone", msg);
    }

    [Fact]
    public void The_uid_map_message_says_container_flags_will_not_help()
    {
        // The restriction is enforced on the host and targets exactly the
        // unconfined processes `apparmor=unconfined` produces, so the obvious
        // next move is the wrong one and the message has to say so.
        var msg = PythonHandler.SandboxSetupError(UidMapDenied);

        Assert.Contains("do NOT help", msg);
        Assert.Contains("HOST", msg);
    }

    [Fact]
    public void The_generic_message_lists_the_knob_per_distro()
    {
        // Reached when bwrap fails some other way. It used to name only the
        // Debian knob, which is wrong on the newest Ubuntu and on RHEL.
        var msg = PythonHandler.SandboxSetupError("bwrap: No permissions to create new namespace");

        Assert.Contains("apparmor_restrict_unprivileged_userns", msg);
        Assert.Contains("unprivileged_userns_clone", msg);
        Assert.Contains("user.max_user_namespaces", msg);
    }

    [Fact]
    public void A_mount_failure_keeps_its_own_message()
    {
        // Docker's masked /proc — a different cause with a different fix, and
        // it must not be swallowed by the uid-map branch added in front of it.
        var msg = PythonHandler.SandboxSetupError("bwrap: Can't mount proc on /newroot/proc");

        Assert.Contains("systempaths=unconfined", msg);
        Assert.DoesNotContain("apparmor_restrict_unprivileged_userns", msg);
    }
}
