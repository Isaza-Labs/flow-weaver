using flow_weaver_backend.Services.Worker.Handlers;

namespace flow_weaver_backend.Tests;

// Guards the network-enabled escape hatch: netmiko/paramiko/socket are blocked
// in the default sandbox and allowed ONLY when network_enabled is set, while
// exec/eval/open/os stay blocked regardless.
public class PythonSandboxTests
{
    [Theory]
    [InlineData("from netmiko import ConnectHandler")]
    [InlineData("import paramiko")]
    [InlineData("import socket")]
    public void Network_modules_blocked_by_default(string script)
    {
        Assert.NotNull(PythonHandler.CheckDangerousCode(script, networkEnabled: false));
    }

    [Theory]
    [InlineData("from netmiko import ConnectHandler")]
    [InlineData("import paramiko")]
    [InlineData("import socket, time")]
    public void Network_modules_allowed_when_network_enabled(string script)
    {
        Assert.Null(PythonHandler.CheckDangerousCode(script, networkEnabled: true));
    }

    [Theory]
    [InlineData("import os")]            // OS introspection — never allowed
    [InlineData("import subprocess")]
    [InlineData("import requests")]
    [InlineData("x = open('/etc/passwd')")]  // file access — never allowed
    [InlineData("eval('1+1')")]
    public void Dangerous_always_blocked_even_network_enabled(string script)
    {
        Assert.NotNull(PythonHandler.CheckDangerousCode(script, networkEnabled: true));
    }

    [Fact]
    public void Base_safe_modules_allowed_in_both_modes()
    {
        const string script = "import json, re\nfrom datetime import datetime";
        Assert.Null(PythonHandler.CheckDangerousCode(script, networkEnabled: false));
        Assert.Null(PythonHandler.CheckDangerousCode(script, networkEnabled: true));
    }

    // Spellings that used to walk straight past this filter. Python's grammar
    // allows any whitespace after the keyword and puts a statement after `:` in
    // a compound header, but the check matched the literal "import " prefix on a
    // newline-split line — so neither form was ever examined.
    //
    // These are pinned here as well as in the AST guard because a regression in
    // the cheap pass shouldn't be invisible just because the expensive pass
    // still catches it.
    [Theory]
    [InlineData("import\tos")]                    // tab, not space
    [InlineData("import  os")]                    // two spaces
    [InlineData("if 1: import os")]               // compound statement
    [InlineData("for x in []:\n    import socket")]
    [InlineData("from\tos import system")]
    [InlineData("import json;import\tos")]
    public void Whitespace_and_compound_import_forms_are_caught(string script)
    {
        Assert.NotNull(PythonHandler.CheckDangerousCode(script, networkEnabled: false));
    }

    // `imported` / `importlib_name` merely START with the keyword — they are
    // identifiers, not imports, and must not be mistaken for one.
    [Theory]
    [InlineData("imported = 1")]
    [InlineData("fromage = 'brie'")]
    public void Identifiers_that_start_with_a_keyword_are_not_imports(string script)
    {
        Assert.Null(PythonHandler.CheckDangerousCode(script, networkEnabled: false));
    }
}
