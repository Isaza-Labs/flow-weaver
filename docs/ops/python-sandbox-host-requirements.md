# Python sandbox — host requirements and the security decision

FlowWeaver runs `python_snippet` steps inside a bubblewrap sandbox. Bubblewrap
needs to create a **user namespace**, and several distributions restrict that by
default. On a host where it is restricted, every python step fails with:

```
bwrap: setting up uid map: Permission denied
```

There are four ways to resolve it. They are **not** equivalent in risk, and the
gap between the best and the worst is large enough that this deserves a decision
rather than a default. This document exists so that decision is made with the
consequences visible, and — for a customer host — signed off by whoever owns it.

## The decision in one table

| Option | Host change | Residual risk | Use when |
|---|---|---|---|
| **1. Don't run python snippets** | none | none — the feature is off | The deployment doesn't use `python_snippet` |
| **2. AppArmor profile for bwrap** | one file | low — one binary is exempted | **Default recommendation** |
| **3. Global sysctl** | one line | moderate — host-wide | Dedicated appliance host |
| **4. Privileged worker container** | none | **high** — see below | Policy forbids host changes, with compensations |

`sudo ./deploy/setup-host.sh` implements 2, falling back to 3. It detects which
restriction applies, verifies the result, and is idempotent.

## What the restriction is actually protecting

Unprivileged user namespaces grant a local user `CAP_SYS_ADMIN` **inside** the
namespace. That unlocks kernel code paths which were previously root-only —
`mount`, overlayfs, netfilter/nftables — and a substantial share of Linux local
privilege-escalation vulnerabilities in recent years required exactly that
primitive. Ubuntu enabled the restriction (`kernel.apparmor_restrict_unprivileged_userns=1`,
shipped in `/usr/lib/sysctl.d/10-apparmor.conf`) after repeated LPEs.

So this is real kernel attack surface. What you are choosing is **who gets it**.

## Why the worker is the wrong component to weaken

From the handler's threat model: the worker is where untrusted snippets execute,
and it sits next to the credential keyring, the secrets it can decrypt, the
database, and the management network that reaches customer devices.

That makes the blast radius asymmetric:

```
without option 4:  snippet escapes bwrap → a constrained container → host still distant
with option 4:     snippet escapes bwrap → CAP_SYS_ADMIN, no seccomp → host root
```

Option 4 converts a layered defence into a single point of failure, in the one
component whose compromise matters most.

---

## Option 1 — don't run python snippets

The lowest-risk answer, and the one most often overlooked.

Remove the `sandbox` tag from the worker's `Worker__Tags`. `python_snippet`
steps are enqueued with that tag exclusively, so nothing claims them and the
sandbox never runs. Everything else — `ssh`, `rest_call`, `integration_action`,
`ansible`, `netconf`, reports, email — is unaffected.

Take this when the deployment does not author python snippets. It removes the
question rather than answering it.

## Option 2 — AppArmor profile for bwrap (recommended)

Grants user-namespace creation to the bubblewrap binary alone. Every other
binary on the host stays under the distribution's restriction, so a compromised
unrelated service gains nothing.

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

**Residual risk:** processes launched through bwrap can create user namespaces.
That is the feature being asked for, and it is scoped to one binary path rather
than the whole system.

> **Caveat for the Docker deployment:** the profile attaches by *path* to the
> host's `/usr/bin/bwrap`. The bwrap that python_snippet runs is the worker
> container's own copy, which lives under an overlayfs path the profile does
> not match — so on a containerized deployment the profile can fix the host
> binary while the worker's stays blocked. This is why verification must run
> *inside* the worker (`setup-host.sh` does this automatically when the
> container is running, and falls back to option 3 if the profile falls
> short). Option 2 as written is fully effective for the **native** (non-
> Docker) deployment.

## Option 3 — relax the sysctl globally

```bash
echo 'kernel.apparmor_restrict_unprivileged_userns=0' \
  | sudo tee /etc/sysctl.d/99-flowweaver-userns.conf
sudo sysctl --system
```

**Residual risk:** the kernel surface described above is restored for *every*
process on the host, not just bwrap. Acceptable on a dedicated appliance host
with no untrusted local users and no other workloads. Weak on a shared host.

Note the second-order effect: a snippet that escaped bwrap lands in the worker
container, where userns is now available to attempt kernel LPE against the host.
Option 3 marginally helps the attacker in precisely the scenario you care about.
Option 2 does not have this property in the same degree, because the exemption
is bound to the bwrap binary.

## Option 4 — privileged worker, no host change

Technically viable and **measured**, not assumed:

| Attempt | Result |
|---|---|
| `setcap cap_sys_admin+ep /usr/bin/bwrap` | Rejected by bwrap: `Unexpected capabilities but not setuid`. File capabilities are not supported. |
| setuid bwrap + `cap_add: SYS_ADMIN` | `bwrap: capset failed` — insufficient. |
| setuid bwrap + `SYS_ADMIN, NET_ADMIN, SYS_PTRACE, SYS_CHROOT` + `seccomp=unconfined` | Works, including with **no user namespace at all** — which is what makes it independent of the host sysctl. |

Recipe: `chmod u+s /usr/bin/bwrap` in the worker image, plus those four
capabilities and `seccomp=unconfined` on the worker service.

### What you are accepting

- **`CAP_SYS_ADMIN` in a container is, in practice, root on the host.** It is a
  long-documented escape vector.
- **`seccomp=unconfined`** removes the default syscall filter, widening kernel
  attack surface from inside the container considerably.
- **`SYS_PTRACE`** lets a process trace others *in the same container* —
  including the worker, which holds decrypted credentials in memory. A partial
  sandbox escape becomes direct credential theft.
- **setuid-root bwrap in the image** puts a root-privileged helper within reach
  of any code execution inside the worker.
- `network_enabled` snippets already run with host networking. Combined with
  this option, the path from such a snippet to the host is very short.

### Compensating controls (mandatory if option 4 is chosen)

- Worker on a **dedicated host or VM**, no other workloads.
- Network segmentation from that host to the rest of the estate.
- **`network_enabled` snippets disabled entirely** on that deployment.
- Snippet authoring and keyring access restricted to a minimal set of admins.
- Treat the worker host as an equivalent trust boundary to a device jump host.

---

## What does not change under any option

The sandbox's guarantees on the normal path are unaffected: read-only binds of
`/usr`, `/lib`, `/lib64`; a private `/tmp` tmpfs; `--cap-drop ALL` for the
sandboxed process; no network unless the snippet is explicitly network-enabled;
`prlimit` caps on memory, CPU and file size; and the AST-based import guard that
runs before the sandbox is built.

## Verification

After applying any of options 2–4:

```bash
docker compose -f deploy/docker-compose.yml restart worker
docker exec -u app <worker> bwrap --ro-bind / / --unshare-user true && echo OK
```

Then run any `python_snippet` workflow. A failure at this point reports the
specific signal or setup error with the knob to turn — see
`docs/handlers/python_snippet.md`.

## Knob by distribution

Getting this wrong is the usual time sink, because the name differs and the
wrong one simply does not exist:

| Distribution | Sysctl | Permissive value |
|---|---|---|
| Ubuntu 23.10+ / 24.04 | `kernel.apparmor_restrict_unprivileged_userns` | `0` |
| Older Debian / Ubuntu | `kernel.unprivileged_userns_clone` | `1` |
| RHEL and derivatives | `user.max_user_namespaces` | `15000` |

```bash
sysctl -a 2>/dev/null | grep -E 'userns|user_namespaces'
```

## Sign-off

For a customer deployment, record which option was chosen, by whom, and the date
— together with the compensating controls if option 4 was taken. The choice is
the customer's to make on their own infrastructure; this document is what makes
it an informed one.
