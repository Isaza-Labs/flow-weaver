#!/usr/bin/env python3
"""Static safety check for a `python_snippet` body, run BEFORE the sandbox.

Why this exists
---------------
The check this replaces lived in C# and matched string prefixes on the source
(`line.StartsWith("import ")`, `script.Contains("exec(")`). Python's grammar is
not prefix-matchable, so every one of these slipped through untouched:

    import\tos              # tab instead of a space
    if 1: import os         # compound statement
    import (os)             # parenthesised
    from  os  import system # two spaces
    getattr(__builtins__, "ev" + "al")("...")

Parsing the real grammar removes that whole class of bypass at once: `ast.parse`
sees an `Import` node no matter how the statement is spelled.

`ast.parse` builds a tree. It does NOT import, evaluate or execute anything, so
running this on a hostile script is safe — that's the property that lets the
check happen before the sandbox is even built.

This is defence in depth, not the boundary. The boundary is bwrap
(`--unshare-all`, `--cap-drop ALL`, read-only /usr). This layer's job is to keep
`os` / `socket` / `subprocess` out of the sandbox in the first place, which
matters most for `network_enabled` snippets whose sandbox deliberately keeps the
host network.

Protocol
--------
stdin  : {"script": "<source>", "allowed": ["json", "re", ...]}
stdout : {"ok": true}
         {"ok": false, "error": "<human-readable reason>"}
exit 0 in both cases; a non-zero exit means the guard itself broke and the
caller must fail closed.
"""

import ast
import json
import sys

# Callables that hand a script a way to reach code or resources the import
# allow-list is meant to gate. Blocked by NAME, wherever they appear — as a
# direct call, an attribute, or a string handed to getattr.
#
# `getattr`/`setattr` are here because `getattr(__builtins__, "ev"+"al")` is the
# standard way around a name-based ban; snippets have no legitimate need for
# dynamic attribute access, so refusing it outright is cheaper than trying to
# decide which uses are safe.
FORBIDDEN_NAMES = frozenset({
    "eval", "exec", "compile", "__import__", "open",
    "getattr", "setattr", "delattr", "vars", "globals", "locals",
    "memoryview", "breakpoint", "input",
})

# Attribute/identifier fragments that reach the interpreter's own internals.
# `__builtins__`, `__subclasses__` and `__globals__` are the three rungs of the
# classic sandbox-escape ladder.
FORBIDDEN_ATTRS = frozenset({
    "__builtins__", "__subclasses__", "__globals__", "__code__",
    "__bases__", "__mro__", "__loader__", "__spec__", "__class__",
})


class _Checker(ast.NodeVisitor):
    def __init__(self, allowed):
        self.allowed = allowed
        self.error = None

    def fail(self, message, node):
        # First failure wins; later ones are noise once the script is refused.
        if self.error is None:
            line = getattr(node, "lineno", "?")
            self.error = f"line {line}: {message}"

    # ── imports ──────────────────────────────────────────────────────
    def visit_Import(self, node):
        for alias in node.names:
            self._check_module(alias.name, node)
        self.generic_visit(node)

    def visit_ImportFrom(self, node):
        # `from . import x` / `from .. import y` — a relative import has no
        # module to check and can only reach files next to the script, which in
        # the sandbox is a private tmpfs holding nothing else. Still refused:
        # it is never something a legitimate snippet needs, and allowing it
        # would mean reasoning about the mount layout to stay correct.
        if node.level:
            self.fail("relative imports are not allowed", node)
            return
        self._check_module(node.module or "", node)
        self.generic_visit(node)

    def _check_module(self, dotted, node):
        # Only the top-level package is gated, matching how Python resolves
        # `os.path` → the `os` package.
        top = dotted.split(".", 1)[0].strip()
        if not top:
            self.fail("import with no module name", node)
        elif top not in self.allowed:
            self.fail(f"imports disallowed module '{top}'", node)

    # ── dangerous names ──────────────────────────────────────────────
    def visit_Name(self, node):
        if node.id in FORBIDDEN_NAMES:
            self.fail(f"uses disallowed builtin '{node.id}'", node)
        elif node.id in FORBIDDEN_ATTRS:
            self.fail(f"uses interpreter internal '{node.id}'", node)
        self.generic_visit(node)

    def visit_Attribute(self, node):
        # Catches `io.open(...)`, `x.__class__`, `().__class__.__bases__`, and
        # `obj.__getattribute__`. The base allow-list includes `io`, so without
        # this the `open` ban would be one attribute access away from useless.
        if node.attr in FORBIDDEN_NAMES:
            self.fail(f"calls disallowed attribute '{node.attr}'", node)
        elif node.attr in FORBIDDEN_ATTRS:
            self.fail(f"reaches interpreter internal '{node.attr}'", node)
        self.generic_visit(node)


def check(script, allowed):
    try:
        tree = ast.parse(script)
    except SyntaxError as exc:
        # A snippet that doesn't parse can't run either. Refusing here turns a
        # runtime traceback into an actionable message with a line number.
        return f"script is not valid Python: line {exc.lineno}: {exc.msg}"
    except (ValueError, RecursionError) as exc:
        # Null bytes, or an expression nested deeply enough to blow the parser's
        # stack. Both are hostile-input shapes, not honest snippets.
        return f"script could not be parsed: {exc}"

    checker = _Checker(frozenset(allowed))
    checker.visit(tree)
    return checker.error


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception as exc:  # noqa: BLE001 — any failure here is fatal
        print(json.dumps({"ok": False, "error": f"guard input invalid: {exc}"}))
        return 0

    error = check(payload.get("script") or "", payload.get("allowed") or [])
    print(json.dumps({"ok": error is None, "error": error} if error
                     else {"ok": True}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
