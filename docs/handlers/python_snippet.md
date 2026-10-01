# `python_snippet` handler

Runs an operator-supplied Python script with a JSON payload on stdin. The
script body is stored on the `Snippet` row; per-step `InputPayload`
becomes the `get_input()` dict; the script returns data via
`set_output(...)` from the bundled `flowweaver_runtime` helper.

This document describes the **defenses** the handler applies, in the
order they fire, and how to operate the sandbox.

## Threat model

The platform may run scripts authored by:

1. The agent (acting on behalf of an authorised user).
2. An administrator hand-writing a snippet.

Either way the script is **untrusted code running on your infrastructure**, and
the sandbox is not optional. The handler does **not** assume the script is benign:

- The agent can be tricked into emitting a malicious snippet via prompt injection.
- An administrator account can be compromised, and a snippet is a durable
  foothold: it runs on the worker, on a schedule or a webhook, long after the
  session that created it is gone.
- A snippet runs where the worker runs — next to the credential keyring, the
  encrypted secrets it can decrypt, the database, and the management network the
  platform reaches devices on. Left unconfined it could read secrets off disk,
  scan or pivot into that private network, or exfiltrate whatever it collected
  to an outside host.

Everything below is what keeps a script to its declared job: transform its input
and return its output.

## Layers of defense

The handler stacks three layers. Each is independent — bypassing one
should still leave the others as obstacles.

### 1. Static allow-list

Two passes run before the sandbox is even built.

**Pre-filter** — `PythonHandler.CheckDangerousCode` and `SafeModules`. Cheap,
no process spawn, rejects the common mistake with a friendly message. It
matches string shapes against source text, which is **not** sufficient on its
own (see below) and is documented in code as non-authoritative. Do not harden
it by adding substring patterns.

**Authoritative pass** — `deploy/python/flow_weaver_import_guard.py`, run via
`python3` with the script on stdin. It `ast.parse`s the snippet and walks the
tree:

- Every `import` / `from … import`, validated against the allow-list by
  top-level package. Relative imports are refused outright.
- `eval`, `exec`, `compile`, `__import__`, `open`, `getattr`, `setattr`,
  `globals`, `locals`, `vars`, `breakpoint` — by name, wherever they appear.
- `__builtins__`, `__subclasses__`, `__globals__`, `__class__`, `__bases__`,
  `__mro__` — the rungs of the classic sandbox-escape ladder.
- Notable module rejects: `os`, `subprocess`, `socket`, `urllib`, `requests`,
  `httpx`, `pathlib`, `ctypes`, `pickle`.

`ast.parse` builds a tree; it never imports, evaluates or executes anything, so
running it on a hostile script outside the sandbox is safe — and a rejected
script costs no namespace setup.

> **Why a real parser.** Comparing source text against `"import "` and
> `"exec("` does not follow Python's grammar, so `import<TAB>os`,
> `if 1: import os`, `import (os)`, `from  os  import system` and
> `getattr(__builtins__, "ev" + "al")` would all pass a substring check
> unexamined. Parsing the grammar removes that entire class of bypass at once.

**Fails closed.** If the guard is missing, times out, or answers something
unparseable, the snippet is **refused** outside Development — "we couldn't
check" must never mean "run it anyway". `Python:ImportGuardPath` overrides the
location (default `/usr/local/lib/flow_weaver_import_guard.py`, shipped by
`deploy/Dockerfile`). Development falls back to the pre-filter alone so local
iteration doesn't need the worker image.

An admin can widen the list from `/admin/python-packages`; a PyPI
package is installed by the worker into `<Python:PackagesDir>/site`
(`/app/pyenv/site` in the shipped image, a shared named volume) and that
directory is `--ro-bind`ed into the sandbox and prepended to
`PYTHONPATH`. Only modules in state `ready` pass the guard, and the
install itself never runs inside the sandbox.

Limits: the allow-list is a defense, not the defense. It governs what a script
may *import*; the kernel sandbox below is what governs what it can *reach*.
This layer matters most for `network_enabled` snippets, whose sandbox
deliberately keeps the host network.

### 2. Kernel sandbox

Configured by `Python:SandboxMode`. Default is `bwrap` outside
Development.

| Mode | Behaviour |
|---|---|
| `none` | Bare `python3`. Refused outside Development; safe only when the static check is the only barrier you trust. |
| `bwrap` | Bubblewrap namespace sandbox. RO bind `/usr`, `/lib`, `/lib64`, the script path; private `/tmp`; `--unshare-all` (no network, no IPC, no UTS, no PID); `--cap-drop ALL`; `--die-with-parent`. No `/etc/resolv.conf` so DNS does not resolve even if the script reaches `socket`. |
| `nsjail` | Admin-supplied config via `Python:NsjailConfig`. Use when you need finer syscall filtering. |
| `custom` | Legacy passthrough via `Python:SandboxCommand` (`;`-separated argv). |

The bwrap default ships with `apt-get install bubblewrap` in
`deploy/Dockerfile` and is layered into `prlimit` (see below).

#### Concurrency throttle + transient-setup retry

Every bwrap/nsjail launch creates fresh namespaces. The host caps how
many user namespaces can exist at once via `user.max_user_namespaces`,
which is **per user-namespace and shared by every process in the worker
container**. A per-device fan-out becomes one `step` job per device and
the worker runs up to `Worker:MaxConcurrency` (default 10) at once, so on
a host with a modest limit some launches win the race to allocate a
namespace and the rest die with

```
bwrap: No permissions to create new namespace, likely because the
kernel does not allow non-privileged user namespaces.
```

The kernel is fine — the namespace table is momentarily full. This is
the "some devices succeed, others fail with this exact message in the
same run" pattern. `PythonHandler` defends against it in-process so no
host change is required:

| Knob | Default | Effect |
|---|---|---|
| `Python:MaxConcurrentSandboxes` | 4 | Process-wide cap on simultaneous namespace-creating launches. Lower it on a host with a tight `user.max_user_namespaces`; raise it once the host sysctl is bumped. |
| `Python:SandboxSetupRetries` | 4 | Extra attempts when a launch fails to create its namespace. Backoff is exponential-ish with jitter (~100→1000 ms). |

Retrying a setup failure is **safe**: it happens before `python3` starts,
so the script never ran — no side effects, no idempotency concern (even
though the handler defaults to `RequiresCompensation`). The retry only
fires when the process exited non-zero, produced **no stdout**, and the
stderr names the namespace *allocation* step; a real script error is
returned untouched. When retries are exhausted the raw bwrap text is
replaced with an actionable message naming the host sysctls to turn.

A `setting up uid map: Permission denied` is deliberately **not** retried — it
is fixed host policy rather than contention, so it is reported at once. See the
section on it below.

> Two throttles compose here: `Python:MaxConcurrentSandboxes` is the
> process-wide cap on concurrent bwrap launches (across all snippets),
> while a snippet's `MaxParallel` caps concurrent executions of that one
> snippet (enforced by the worker). For namespace pressure the process-wide
> cap is the blunt lever; lowering a hot snippet's `MaxParallel` is the
> targeted one.

#### Queue routing: the `sandbox` tag

`python_snippet` step jobs are enqueued with the dedicated queue tag
`sandbox` (every other step type uses `default`). Only containers whose
seccomp/AppArmor is relaxed for bwrap should claim it — in the compose
deploy that is the `worker` service alone; the API/backend container
overrides `Worker__Tags` to `default` + `orchestrator` so it never picks
up a python step it cannot sandbox. The backend keeps Docker's default
seccomp profile, which blocks user-namespace creation, so if both
containers claimed python steps they would fail **intermittently** with
`No permissions to create new namespace`, depending on which container
won the claim race.

A single-process setup (dev, or a deploy without a separate worker) works
unchanged: when no tags are configured, the effective claim list is all
three tags (`WorkerOptions.EffectiveTags`). If you override tags manually,
make sure **at least one** running container claims `sandbox`, or python
steps will sit in the queue forever.

To verify what a container actually claims, check its boot log line:

```
Worker <id> started — tags=[default, orchestrator]
```

The backend must NOT list `sandbox`; the worker must. (Implementation
note: the bound `Worker:Tags` property deliberately defaults to *empty* —
.NET's config binder can only append to a pre-initialized array, so a
non-empty default could never be shrunk by an operator override.)

### 3. `prlimit` resource caps

Wraps the sandbox invocation when `prlimit` is on `PATH` (provided by
`util-linux` in the image). Applies:

| Resource | Default | Knob |
|---|---|---|
| Virtual memory (`--as`) | 256 MiB | `Python:MaxMemoryMb` |
| CPU time (`--cpu`) | min 1 sec, falls back to script's wall-clock timeout | step `timeout_seconds` |
| File size (`--fsize`) | 10 MiB | hard-coded |
| Process count (`--nproc`) | 1024 | `Python:MaxProcesses` |

Caps are enforced by the kernel. They survive a sandbox bypass (the
namespace can be exited but the rlimit cannot).

**Notes:**

- `RLIMIT_CPU` measures **CPU time**, not wall-clock. A multi-threaded
  script can burn 60 CPU-seconds in 30 wall-seconds and get killed
  early; conversely, an I/O-bound script idle on a syscall does not
  accumulate CPU time. Wall-clock enforcement is handled separately by
  the C# `CancellationTokenSource` and triggers at the same threshold.
- `RLIMIT_NPROC` (`--nproc`, `Python:MaxProcesses`) is **per Linux user**,
  not per process — it counts the worker's OWN threads (the .NET runtime
  holds dozens) plus every concurrent sandbox. A cap below the worker's
  baseline thread count makes bwrap's `clone()` for namespace setup fail
  intermittently with EAGAIN
  (`Creating new namespace failed: Resource temporarily unavailable`).
  The default is 1024; raise it on a busy worker, lower it only to
  tighten the fork-bomb cap.

## Wall-clock timeout

`PythonHandler` enforces a wall-clock timeout via a linked
`CancellationTokenSource`. When it fires the entire process tree is
killed and the call returns
`exit code -1: python process timed out after Ns`. This is the last
line of defense if a script gets past the prlimit (e.g. busy-spinning
on memory-resident state without exceeding `--as`).

Default 60 s, clamped to `[5, 600]`. Set per-snippet via
`SnippetTimeoutSeconds` or per-step via the `timeout_seconds` input.

## `setting up uid map: Permission denied` (Ubuntu 23.10+)

Distinct from *"could not create a namespace"*, and the distinction is the whole
diagnosis: the namespace **was** created and writing `/proc/self/uid_map` was
refused. On Ubuntu 23.10 / 24.04 that is AppArmor's unprivileged-userns
restriction, shipped on by default in `/usr/lib/sysctl.d/10-apparmor.conf`.

Check on the **host**, not inside the container:

```bash
sysctl kernel.apparmor_restrict_unprivileged_userns    # 1 = this is your problem
```

Two ways out.

**Allow it globally** — simplest, and what most deployments do:

```bash
echo 'kernel.apparmor_restrict_unprivileged_userns=0'   | sudo tee /etc/sysctl.d/99-flowweaver-userns.conf
sudo sysctl --system
```

**Keep the hardening, exempt only bwrap** — narrower, and preferable on a host
that runs more than FlowWeaver:

```bash
sudo tee /etc/apparmor.d/bwrap >/dev/null <<'EOF'
abi <abi/4.0>,
include <tunables/global>

profile bwrap /usr/bin/bwrap flags=(unconfined) {
  userns,
  include if exists <local/bwrap>
}
EOF
sudo systemctl reload apparmor
```

> **Container-level flags do not help.** `security_opt: [apparmor=unconfined]`
> makes the process *unconfined*, and unconfined processes are exactly what this
> restriction targets. The policy is enforced on the host; it has to be changed
> there.

### One command instead of research

`sudo ./deploy/setup-host.sh` detects which of the three restrictions applies,
applies the narrowest fix that works (the bwrap AppArmor profile in preference
to relaxing the sysctl globally), and verifies with a bwrap smoke test. It is
idempotent and reports "already ok" when there is nothing to do. The operator
runs one command without having to know which knob their distro uses.

They do not have to discover the problem on their own either: `deploy/run.sh`
runs a bwrap smoke test **inside the worker container** after every `up`
(uid map + fresh `/proc` mount, as the non-root worker user) and, when it
fails, prints the `setup-host.sh` command instead of leaving the failure to
surface on the first python step.

### No host changes at all

> The full decision — all four options with their security implications and the
> compensating controls option 4 requires — is in
> [`docs/ops/python-sandbox-host-requirements.md`](../ops/python-sandbox-host-requirements.md).
> Use that one for a customer sign-off; the summary below is the engineering
> shorthand.

Possible, and measurably expensive. bwrap needs privilege from *somewhere*; if
the host will not grant unprivileged user namespaces, the only remaining source
is the container. Verified combinations:

| Attempt | Result |
|---|---|
| `setcap cap_sys_admin+ep /usr/bin/bwrap` | **Rejected by bwrap**: `Unexpected capabilities but not setuid`. It does not support file capabilities. |
| setuid bwrap + `cap_add: SYS_ADMIN` | `bwrap: capset failed` — it needs more than SYS_ADMIN. |
| setuid bwrap + `SYS_ADMIN, NET_ADMIN, SYS_PTRACE, SYS_CHROOT` + `seccomp=unconfined` | **Works**, including with no user namespace at all — which is what makes it independent of the host sysctl. |

So the recipe is `chmod u+s /usr/bin/bwrap` in the worker image plus those four
capabilities in compose.

**Why it is not the default.** A container holding `CAP_SYS_ADMIN` with seccomp
disabled is, for practical purposes, root on the host. The worker is the process
that executes untrusted snippets, so this grants near-host-root to the component
whose compromise the sandbox exists to contain — the blast radius of an escape
becomes far larger than the restriction being worked around. Relaxing one sysctl
on the host, or granting `userns` to one binary via AppArmor, is a much smaller
change than making the worker container privileged.

Take it only when a customer's policy forbids host changes outright, and say
plainly what was traded.

Knob by distro — getting this wrong is the usual time sink:

| Distro | Sysctl | Value that allows it |
|---|---|---|
| Ubuntu 23.10+ / 24.04 | `kernel.apparmor_restrict_unprivileged_userns` | `0` |
| Older Debian / Ubuntu | `kernel.unprivileged_userns_clone` | `1` |
| RHEL and derivatives | `user.max_user_namespaces` | `15000` |

`sysctl -a 2>/dev/null | grep -E 'userns|user_namespaces'` shows which one your
host actually has.

This failure is **not retried** — it is fixed host policy, so the handler
reports it immediately instead of spending four backoff attempts reaching the
same answer.

## Signal deaths (exit 137 and friends)

A process killed by signal `N` is reported as exit code `128+N`, and bwrap
propagates its child's status the same way. The handler decodes these instead of
surfacing the raw number, because the raw number is a dead end: a killed script
produces **no stdout and no stderr** — the signal is uncatchable and
`flowweaver_runtime` flushes its output from an `atexit` hook that therefore
never runs. Every signal death looks identical without decoding.

| Exit | Signal | Source | Meaning |
|---|---|---|---|
| 152 | SIGXCPU | **ours** (`prlimit --cpu` = the step timeout) | The script burned more CPU time than its timeout allows. A script that sleeps or waits on I/O uses almost no CPU, so this means a busy loop. |
| 153 | SIGXFSZ | **ours** (`prlimit --fsize`) | Wrote more than 10 MiB to a file. Snippets return data via `set_output()`; the sandbox's `/tmp` is a private tmpfs that is discarded anyway. |
| 139 | SIGSEGV | interpreter | Native crash, not a Python exception. Suspect a compiled dependency, or an allocation failure the interpreter couldn't report (`Python:MaxMemoryMb`). |
| 137 | SIGKILL | **not ours** | See below. |

**137 is never FlowWeaver.** The wall-clock timeout returns
`python process timed out after Ns` on its own path, and the handler's other
limits raise the different signals above. So a SIGKILL came from the kernel or
from outside the container.

To tell those two apart the handler samples the cgroup's `oom_kill` counter
(`/sys/fs/cgroup/memory.events`, or the v1 equivalent) around the run and states
the verdict in the error:

- **Counter went up** → the kernel OOM-killed it. Raise the worker's memory
  limit, lower `Python:MaxConcurrentSandboxes`, or shrink what the script holds.
- **Counter flat** → not memory. Something outside the container sent SIGKILL.
- **Counter unreadable** → the message says so rather than implying memory was
  ruled out.

For the third case, on the worker's host:

```bash
dmesg -T | tail -50                      # OOM or audit records around the failure
docker events --since 15m                # an external stop/kill of the container
docker exec <worker> cat /sys/fs/cgroup/memory.events

# Definitive: record who sends SIGKILL to anything, then reproduce.
auditctl -a always,exit -F arch=b64 -S kill -F a1=9 -k fw_sigkill
ausearch -k fw_sigkill -i
```

Long-lived sandboxes are the ones exposed to this — a snippet that sleeps for a
minute keeps a bwrap process alive for a minute. Security agents (auditd rules,
falco, SELinux/AppArmor enforcement, EDR) that react to long-running
namespace-creating processes are the usual suspect when the OOM counter is flat.

## Configuration recipes

### Production (default)

```jsonc
// appsettings.json or env
{
  "Python": {
    "SandboxMode": "bwrap",
    "MaxMemoryMb": 256
  }
}
```

### Hardened with a custom nsjail profile

```jsonc
{
  "Python": {
    "SandboxMode": "nsjail",
    "NsjailConfig": "/etc/flow-weaver/nsjail-python.cfg"
  }
}
```

### Local development

```bash
# Disable the sandbox so you don't need bwrap on your dev box.
ASPNETCORE_ENVIRONMENT=Development
Python__SandboxMode=none
```

## Tested attack patterns

The unit suite (`PythonHandlerSandboxTests`) exercises the pre-filter
(`PythonHandler.CheckDangerousCode`). Each rejected script below is also refused
by the authoritative AST guard, which bans the module or the name wherever it
appears:

| Script | Expected outcome |
|---|---|
| `import os; os.system("touch /tmp/owned")` | Rejected — `os` not in allow-list. |
| `__import__("os").system("…")` | Rejected — `__import__` is a banned name. |
| `eval("__import__('os').system('…')")` | Rejected — `eval` is a banned name. |
| `with open("/etc/passwd") as f: ...` | Rejected — `open` is a banned name. |
| Fork bomb via `import os; os.fork()` | Rejected at the static layer; would also be capped by prlimit `--nproc` (`Python:MaxProcesses`, default 1024). |
| Memory bomb `b = bytearray(1 << 40)` | Falls through the static layer; killed by prlimit `--as=`. |

The runtime layer (bwrap behaviour and namespace properties) is not covered by
the automated tests. Verify it on the worker image: `deploy/run.sh` runs a bwrap
smoke test inside the worker after every `up`, and the command under
"Operator playbook" below reproduces it by hand.

## Operator playbook

- **A snippet times out unexpectedly**: the prlimit `--cpu=` cap kicks
  in at the wall-clock timeout. Raise the snippet's `timeout_seconds`
  before raising `Python:MaxMemoryMb`.
- **A snippet OOMs**: bump `Python:MaxMemoryMb`. Avoid going past
  1024 MiB without first auditing why the script needs that much.
- **Snippet works locally but fails in prod**: check the log for the
  refusal string `python_snippet sandbox disabled outside Development`.
  The local box probably has `SandboxMode=none` set; production
  requires the sandbox.
- **Bubblewrap missing**: the image must include the `bubblewrap` apt
  package. If you forked the image, re-pull `deploy/Dockerfile`.
- **`... could not create a namespace` — but only SOMETIMES**: a
  container **without** the seccomp/AppArmor relaxations is claiming
  `sandbox` jobs. The error message names the failing container
  (`container '<hostname>'`) — compare it against `docker ps`. In the
  standard compose deploy this must be the `worker` service only; check
  the `Worker__Tags` overrides in `deploy/docker-compose.yml` (the
  backend claims `default`+`orchestrator`, the worker also claims
  `sandbox`). See "Queue routing" above.
- **`bwrap: No permissions to create new namespace`**: see the
  concurrency section above. Tell the cause apart with
  `docker exec -u app <worker> bwrap --ro-bind / / --unshare-user true`:
  - **Fails in the container, host has `kernel.unprivileged_userns_clone=1`**
    → the **Docker default seccomp profile** is blocking the non-root
    worker from creating user namespaces (the most common containerized
    case). The shipped `worker` service already sets
    `security_opt: [seccomp=unconfined, apparmor=unconfined, systempaths=unconfined]`;
    make sure the container that claims `sandbox` jobs has those options
    (a customised compose file, or a backend that was given the `sandbox`
    tag, will not). Adding `SYS_ADMIN` does not help: the worker runs as the
    non-root `app` user, so the capability does not reach bwrap. The snippet
    is still isolated by bwrap itself.
  - **Fails on the host too** → unprivileged user namespaces are off at
    the kernel: enable them (Debian/Ubuntu
    `sysctl kernel.unprivileged_userns_clone=1`; RHEL/derivatives
    `sysctl user.max_user_namespaces=15000`).
  - **Only *some* steps fail under a fan-out** → namespace-table
    exhaustion: raise `sysctl user.max_user_namespaces=64000` and/or lower
    `Python:MaxConcurrentSandboxes`. The handler retries transient
    failures, so `... could not create a namespace after retries` means
    it's saturated/blocked, not a one-off race.
