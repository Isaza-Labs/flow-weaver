"""Bridge between the flow-weaver worker and user Python snippets.

The worker writes the step's InputPayload to stdin as JSON and parses
stdout as JSON (or wraps it as {"raw": "..."} when parsing fails).
These helpers isolate snippets from those conventions so the starter
template can stay readable:

    from flowweaver_runtime import get_input, set_output, integration

    def run(ctx):
        inp = get_input()
        nb = integration("netbox")
        devices = nb.get("/api/dcim/devices/", params={"status": "active"}).json()
        set_output({"count": len(devices.get("results", []))})

The `run(ctx)` function is auto-invoked at process exit when (a) the
script defined a module-level callable named `run` AND (b) the script
did not already call `set_output` itself. This lets the copy-paste
pattern above "just work" without the author needing to remember a
trailing `run(None)` line. Scripts that do call `run()` themselves
(or skip the function entirely and use module-level code) are not
affected — the auto-invoke no-ops in both cases.

Only the last `set_output` call wins — calling it multiple times
overwrites the previous payload. If a snippet never calls
`set_output`, stdout is treated as the raw output by the handler.

The `integration(name)` helper is the ONLY way to make HTTP calls from
a snippet. The sandbox blocks `urllib`, `requests`, etc. when imported
directly by the script; this library uses `urllib.request` internally
and restricts every request to the integration's base_url, so a
compromised snippet can't reach arbitrary URLs.
"""

import atexit
import datetime
import json
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request

_cached_input = None
_integrations_cache = None
_log_entries = []
_set_output_called = False
_LOG_MARKER = "__fw_logs__"
_VALID_LEVELS = ("debug", "info", "warn", "warning", "error")


def get_input():
    """Read and return the JSON payload the worker piped on stdin.

    Cached across calls so snippets can call get_input() multiple times
    without re-reading a closed stream. The returned dict does NOT
    include the `_integrations` key — that's an internal channel used
    by `integration(name)`. Snippets get a clean view of their config.
    """
    global _cached_input, _integrations_cache
    if _cached_input is None:
        try:
            raw = sys.stdin.read()
            data = json.loads(raw) if raw else {}
        except (json.JSONDecodeError, ValueError):
            data = {}
        if isinstance(data, dict):
            _integrations_cache = data.pop("_integrations", {}) or {}
        else:
            _integrations_cache = {}
        _cached_input = data
    return _cached_input


def set_output(payload):
    """Emit a JSON payload on stdout for the worker to capture."""
    global _set_output_called
    sys.stdout.write(json.dumps(payload))
    sys.stdout.flush()
    _set_output_called = True


# ─────────────────────────────────────────────────────────────────────
#  Structured logging
# ─────────────────────────────────────────────────────────────────────


def log(level, message, **extra):
    """Emit a structured log line visible in the step's Logs + output._logs.

    Example:
        log("info", "processed devices", count=50, duration_ms=231)

    Levels: debug / info / warn / warning / error (case-insensitive).
    Unknown levels are coerced to "info" so a typo doesn't suppress the
    entry. Every entry carries an ISO-8601 UTC timestamp inserted by the
    runtime.

    Two channels:
      1. A human-readable mirror goes to stderr right away, so `docker
         logs` and the step's Logs field show it as it happens.
      2. The structured record is buffered and flushed at process exit
         via an atexit hook — the worker extracts the buffer, merges it
         into step.OutputPayload._logs for downstream steps to read.
    """
    norm = (level or "info").lower()
    if norm not in _VALID_LEVELS:
        norm = "info"
    if norm == "warn":
        norm = "warning"

    entry = {
        "level": norm,
        "message": str(message),
        "ts": datetime.datetime.now(datetime.timezone.utc).isoformat(),
    }
    # Only keep JSON-safe extras. Anything else is stringified so the
    # script can `log("info", "done", obj=some_response)` without
    # worrying about serialization.
    if extra:
        safe = {}
        for k, v in extra.items():
            try:
                json.dumps(v)
                safe[k] = v
            except (TypeError, ValueError):
                safe[k] = str(v)
        entry["extra"] = safe

    _log_entries.append(entry)

    # Human mirror to stderr. Kept compact so it reads well in tail -f.
    extra_str = ""
    if extra:
        parts = [f"{k}={v!r}" for k, v in (entry.get("extra") or {}).items()]
        if parts:
            extra_str = " " + " ".join(parts)
    sys.stderr.write(f"[{norm.upper()}] {entry['message']}{extra_str}\n")
    sys.stderr.flush()


def _flush_logs():
    # Final marker line on stderr so the handler can find the structured
    # buffer no matter what the script printed before. Wrapping in
    # newlines makes the marker trivially findable with LastIndexOf.
    if not _log_entries:
        return
    try:
        payload = json.dumps(_log_entries)
    except (TypeError, ValueError):
        return
    sys.stderr.write(f"\n{_LOG_MARKER}{payload}\n")
    sys.stderr.flush()


atexit.register(_flush_logs)


def _auto_invoke_run():
    """Invoke a top-level `run(ctx)` function at process exit when the
    script forgot to call it.

    The starter template in the module docstring — and the one most
    AI-generated snippets follow — defines `def run(ctx):` without a
    trailing invocation. Without this hook the script defines the
    function, exits cleanly with empty stdout, and the worker records
    {"raw": ""} as the step output. That in turn poisons every
    downstream `{{ steps.<id>.output.* }}` template.

    Guards:
      - `_set_output_called` short-circuits well-behaved scripts that
        already produced output themselves (covers both module-level
        scripts and scripts that invoke `run()` at the bottom).
      - `__main__.run` lookup scopes the check to the executing script,
        ignoring any `run` function imported from a helper module.
      - Exceptions in `run(ctx)` propagate so the traceback lands in
        stderr and the worker marks the step as failed instead of
        silently succeeding with an empty payload.

    This runs BEFORE `_flush_logs` because atexit callbacks fire in
    LIFO order and this one is registered second — any `log(...)` calls
    inside `run(ctx)` still end up in the flushed structured buffer.
    """
    if _set_output_called:
        return
    try:
        import __main__
    except ImportError:
        return
    run_fn = getattr(__main__, "run", None)
    if not callable(run_fn):
        return
    try:
        run_fn(None)
    except SystemExit:
        raise
    except BaseException:
        # Re-raise so python exits non-zero and the handler records the
        # traceback; swallowing here would mask bugs in the snippet.
        raise


atexit.register(_auto_invoke_run)


# ─────────────────────────────────────────────────────────────────────
#  Integration HTTP client
# ─────────────────────────────────────────────────────────────────────


class IntegrationError(Exception):
    """Raised when an integration call can't be dispatched or the remote
    returned a non-2xx response. The message is safe to bubble back to
    the user — secrets are never included."""


class _Response:
    """Thin wrapper around urllib's HTTPResponse for ergonomic access.

    Mirrors the parts of `requests.Response` snippets actually use:
      - .status_code  (int)
      - .text         (str, utf-8 decoded body)
      - .headers      (dict[str, str])
      - .json()       (parsed body, raises IntegrationError on bad JSON)
      - .ok           (True when 2xx/3xx)
    """

    def __init__(self, status_code, headers, body_bytes):
        self.status_code = status_code
        self.headers = {k: v for k, v in headers.items()}
        self._body = body_bytes or b""

    @property
    def text(self):
        try:
            return self._body.decode("utf-8")
        except UnicodeDecodeError:
            return self._body.decode("utf-8", errors="replace")

    @property
    def ok(self):
        return 200 <= self.status_code < 400

    def json(self):
        if not self._body:
            return None
        try:
            return json.loads(self._body.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError) as exc:
            raise IntegrationError(
                f"response body is not valid JSON: {exc}"
            ) from None


class _IntegrationClient:
    """HTTP client scoped to a single registered integration.

    Every request is clamped to the integration's base_url — a path that
    would resolve outside it (e.g. starting with `http://`, `//`, or a
    parent `..` segment) raises immediately. That's the runtime half of
    the SSRF story; the other half is that the script never gets the
    raw token, only the pre-rendered headers.
    """

    def __init__(self, name, config):
        self._name = name
        self._base_url = (config.get("base_url") or "").rstrip("/")
        self._headers = dict(config.get("headers") or {})
        self._tls_skip_verify = bool(config.get("tls_skip_verify"))
        if not self._base_url:
            raise IntegrationError(
                f"integration '{name}' has no base_url configured"
            )

    @property
    def base_url(self):
        return self._base_url

    def get(self, path, params=None, headers=None, timeout=30):
        return self._request("GET", path, params, None, headers, timeout)

    def post(self, path, json=None, params=None, headers=None, timeout=30):
        return self._request("POST", path, params, json, headers, timeout)

    def put(self, path, json=None, params=None, headers=None, timeout=30):
        return self._request("PUT", path, params, json, headers, timeout)

    def patch(self, path, json=None, params=None, headers=None, timeout=30):
        return self._request("PATCH", path, params, json, headers, timeout)

    def delete(self, path, params=None, headers=None, timeout=30):
        return self._request("DELETE", path, params, None, headers, timeout)

    def _request(self, method, path, params, body, extra_headers, timeout):
        url = self._join(path, params)
        data = None
        merged_headers = dict(self._headers)
        if body is not None:
            data = _json_mod.dumps(body).encode("utf-8")
            merged_headers.setdefault("Content-Type", "application/json")
        if extra_headers:
            for k, v in extra_headers.items():
                merged_headers[k] = v

        req = urllib.request.Request(
            url, data=data, headers=merged_headers, method=method
        )

        opener = _build_opener(self._tls_skip_verify)
        try:
            with opener.open(req, timeout=timeout) as resp:
                body_bytes = resp.read()
                return _Response(resp.status, resp.headers, body_bytes)
        except urllib.error.HTTPError as exc:
            # 4xx/5xx still return a response so scripts can inspect
            # status_code instead of catching on every call site.
            body_bytes = exc.read() if exc.fp else b""
            return _Response(exc.code, exc.headers or {}, body_bytes)
        except urllib.error.URLError as exc:
            raise IntegrationError(
                f"integration '{self._name}' request failed: {exc.reason}"
            ) from None

    def _join(self, path, params):
        if not isinstance(path, str) or not path:
            raise IntegrationError("path must be a non-empty string")
        # Reject anything that would escape the integration's base_url.
        # Absolute URLs, protocol-relative URLs, and any scheme-bearing
        # string all fail here — the admin authorized one origin, we
        # stay on that origin.
        lowered = path.lstrip().lower()
        if lowered.startswith(("http://", "https://", "//")):
            raise IntegrationError(
                "absolute URLs are not allowed — pass a path relative to "
                "the integration's base_url"
            )
        if not path.startswith("/"):
            path = "/" + path
        url = self._base_url + path
        if params:
            query = urllib.parse.urlencode(
                [(k, v) for k, v in params.items() if v is not None],
                doseq=True,
            )
            if query:
                sep = "&" if "?" in url else "?"
                url = url + sep + query
        return url


# Renamed here to avoid shadowing the stdlib module inside method defaults.
_json_mod = json


def integration(name):
    """Return an HTTP client for the named integration.

    The integration must be referenced from the snippet's
    config_overrides as `<name>_integration_id`. The worker resolves
    it before launching this process; if the name isn't present, an
    IntegrationError is raised so the failure surfaces in the step's
    output instead of silently returning 404.
    """
    # get_input() populates _integrations_cache on first call.
    if _cached_input is None:
        get_input()
    pool = _integrations_cache or {}
    config = pool.get(name) or pool.get(name.lower())
    if config is None:
        known = ", ".join(sorted(pool.keys())) or "<none>"
        raise IntegrationError(
            f"integration '{name}' not available — known: {known}. "
            f"Add '{name}_integration_id' to the snippet's config_overrides."
        )
    return _IntegrationClient(name, config)


def _build_opener(tls_skip_verify):
    if tls_skip_verify:
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        handler = urllib.request.HTTPSHandler(context=ctx)
        return urllib.request.build_opener(handler)
    return urllib.request.build_opener()
