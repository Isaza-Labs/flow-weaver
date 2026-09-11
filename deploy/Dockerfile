# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# NuGet restore is the chokepoint on flaky networks: 16 parallel HTTPS
# connections saturate conntrack / MTU edges and every package times
# out. Three knobs together make it robust:
#   - `--disable-parallel`  → one download at a time.
#   - `DOTNET_NUGET_SIGNATURE_VERIFICATION=false` → skip the call to
#     api.nuget.org/v3-index/repository-signatures/... which times out
#     first and cascades into NU1301. Acceptable in CI/dev images;
#     production signing concerns live in our own image signing.
#   - `NUGET_*_TIMEOUT_IN_SECONDS=600` → 10 min per HTTP request /
#     idle stall vs the default 100s / 60s, so a slow ISP doesn't kill
#     the layer.
COPY flow_weaver_backend/flow_weaver_backend.csproj flow_weaver_backend/
ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false \
    NUGET_HTTP_REQUEST_TIMEOUT_IN_SECONDS=600 \
    NUGET_PACKAGE_DOWNLOAD_TIMEOUT_IN_SECONDS=600
RUN dotnet restore flow_weaver_backend/flow_weaver_backend.csproj \
      --disable-parallel \
      --runtime linux-x64

COPY flow_weaver_backend/ flow_weaver_backend/
RUN dotnet publish flow_weaver_backend/flow_weaver_backend.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Runtime tooling for step handlers:
#   - python3      → PythonHandler writes scripts to a temp file and executes
#                    `python3` (matches the binary name the handler calls).
#                    Also used by SshHandler to spawn the Netmiko runner.
#   - python3-pip  → needed to install netmiko/paramiko (SshHandler's backend)
#                    and any future pip-sourced helper. Removed from the
#                    image after install to keep the layer lean.
#   - ansible      → AnsibleHandler invokes `ansible-playbook` as a subprocess.
#   - sshpass      → ansible needs this to use password auth via
#                    ANSIBLE_SSH_PASS (the handler's credential path). Without
#                    it ansible fails with "you must install the sshpass program".
#   - iputils-ping → .NET's System.Net.NetworkInformation.Ping on Linux shells
#                    out to /usr/bin/ping when the process doesn't have
#                    CAP_NET_RAW (we drop root). Without it the handler fails
#                    with "The system's ping utility could not be found".
#   - bubblewrap   → namespace sandbox used by PythonHandler when
#                    Python:SandboxMode=bwrap (the default outside
#                    Development). Read-only /usr /lib /lib64, private
#                    /tmp tmpfs, --unshare-all, --cap-drop ALL.
#   - util-linux   → ships `prlimit`, the kernel-side cap layer
#                    (memory, cpu time, fsize, nproc) PythonHandler
#                    wraps every invocation with.
# --no-install-recommends keeps the image lean; apt cache is purged after.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      python3 python3-pip ansible sshpass iputils-ping \
      bubblewrap util-linux \
 && rm -rf /var/lib/apt/lists/*

# netmiko/paramiko back the SshHandler's Python runner. Pinned to
# netmiko 4.x because the ConnectHandler `auto_connect=False` + the
# host-key policy hook `flow_weaver_ssh_runner.py` uses are stable
# there.
#
# Flag explanation:
#   --break-system-packages → PEP 668: Debian 12+ marks system python3
#     as externally-managed. We accept the override because the image
#     is single-purpose and there's no user python env to protect.
#   --ignore-installed       → ansible (installed via apt above) pulls
#     in `python3-yaml`, which is a Debian-packaged PyYAML WITHOUT a
#     RECORD file. Netmiko transitively wants PyYAML, pip tries to
#     upgrade, and the uninstall step fails with
#       "Cannot uninstall PyYAML X.Y, RECORD file not found"
#     because dpkg-installed packages don't ship pip metadata. With
#     --ignore-installed, pip leaves the apt copy in /usr/lib/.../dist-
#     packages untouched and drops a fresh copy in /usr/local/.../site-
#     packages. Python's import order puts site-packages ahead of
#     dist-packages, so Netmiko + ansible both see a working PyYAML
#     without either clobbering the other.
RUN pip3 install --no-cache-dir --break-system-packages --ignore-installed \
      "netmiko>=4,<5" "paramiko>=3" "textfsm>=1.1" "ntc-templates>=4"

# Ship the flowweaver_runtime bridge (get_input / set_output) on the
# interpreter's import path so snippets written from the starter template
# work out of the box. Stays at /usr/local/lib/... which is persistent
# across python3 invocations and doesn't require PYTHONPATH overrides.
COPY deploy/python/flowweaver_runtime.py /usr/local/lib/flowweaver_runtime.py
# SshHandler spawns this runner directly (outside the PythonHandler
# allowlist). Shipping it under /usr/local/lib keeps it on PYTHONPATH
# for any `from flow_weaver_ssh_runner import …` sanity imports.
COPY deploy/python/flow_weaver_ssh_runner.py /usr/local/lib/flow_weaver_ssh_runner.py
# Helper modules the runner imports for vendor normalization, parser
# dispatch (TextFSM local + upstream + generic KV), and separator-line
# cleaning. Must sit alongside the runner on PYTHONPATH.
COPY deploy/python/flow_weaver_ssh_parsers.py /usr/local/lib/flow_weaver_ssh_parsers.py
COPY deploy/python/flow_weaver_ssh_clean.py /usr/local/lib/flow_weaver_ssh_clean.py
# Local TextFSM bundle: ships templates that override or extend
# ntc-templates upstream (Nokia SR Linux, Risecom, Aruba gaps). The
# parser walks this dir's `index` first, falls back to upstream.
COPY deploy/python/ntc_templates_extra/ /usr/local/lib/ntc_templates_extra/
# python_snippet import guard. PythonHandler runs this (ast.parse only — it
# never executes the snippet) before building the sandbox, to enforce the module
# allow-list against the real Python grammar instead of string prefixes. Its
# absence is fatal outside Development: the handler refuses to run snippets it
# cannot statically check.
COPY deploy/python/flow_weaver_import_guard.py /usr/local/lib/flow_weaver_import_guard.py
RUN chmod 0755 /usr/local/lib/flow_weaver_ssh_runner.py \
                /usr/local/lib/flow_weaver_import_guard.py
ENV PYTHONPATH=/usr/local/lib

# /app/pyenv holds admin-approved pip packages installed at runtime for
# python_snippet (installed under /site). Owned by `app` so the non-root worker
# can write to it; it's a named volume in compose so installs persist.
RUN mkdir -p /app/keyring /app/pyenv \
 && chown -R app:app /app

COPY --from=build --chown=app:app /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DataProtection__KeyRingPath=/app/keyring \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=1

USER app
EXPOSE 8080

ENTRYPOINT ["dotnet", "flow_weaver_backend.dll"]
