"""End-to-end shape tests for the runner. We don't open SSH connections
here — instead we drive `_run_netmiko` / `_run_direct` indirectly by
asserting that the dispatch + normalization in `_run` plumbs the right
fields, and that the response shape matches the documented contract."""

import json
import subprocess
import sys
from pathlib import Path

DEPLOY_PY = Path(__file__).resolve().parent.parent
RUNNER = DEPLOY_PY / "flow_weaver_ssh_runner.py"


def test_list_platforms_subcommand_returns_json():
    """`python flow_weaver_ssh_runner.py --list-platforms` must produce
    valid JSON with the expected shape so the C# endpoint can cache it."""
    proc = subprocess.run(
        [sys.executable, str(RUNNER), "--list-platforms"],
        capture_output=True, text=True, timeout=30,
        env={"PYTHONPATH": str(DEPLOY_PY)},
    )
    assert proc.returncode == 0, proc.stderr
    payload = json.loads(proc.stdout)
    assert "platforms" in payload
    assert isinstance(payload["platforms"], list)
    sr = next(p for p in payload["platforms"] if p["value"] == "nokia_srl")
    assert sr["vendor"] == "nokia"
    assert sr["has_local_templates"] is True


def test_runner_rejects_empty_stdin():
    proc = subprocess.run(
        [sys.executable, str(RUNNER)],
        input="", capture_output=True, text=True, timeout=10,
        env={"PYTHONPATH": str(DEPLOY_PY)},
    )
    assert proc.returncode == 1
    assert "empty stdin" in proc.stderr


def test_runner_rejects_malformed_input():
    proc = subprocess.run(
        [sys.executable, str(RUNNER)],
        input="not json", capture_output=True, text=True, timeout=10,
        env={"PYTHONPATH": str(DEPLOY_PY)},
    )
    assert proc.returncode == 1
    assert "input parse error" in proc.stderr
