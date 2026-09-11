using flow_weaver_backend.Data.Db;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnippetModel = flow_weaver_backend.Models.Snippet;

namespace flow_weaver_backend.Services.Ai.Seed;

// One-shot seed of the baseline Snippets so the agent doesn't have to
// create a `ping` / `rest_call` snippet from scratch the first time
// someone asks for a ping workflow.
//
// Idempotent: we match by Type — if a snippet of that type already
// exists, we skip it even if the admin renamed it. Custom snippets with
// custom types are never touched.
//
// Runs at boot in Program.cs, same place as the catalog + default agent
// seeds.
public static class DefaultSnippetsSeedService
{
    // The list we seed — small on purpose. Anything more specific (ansible,
    // netconf with a real script) is the admin's job; we only populate the
    // obvious wins so the agent has them as building blocks.
    //
    // InputSchemaJson / OutputSchemaJson are optional. When null we fall
    // back to `{"type":"object"}` — the default that every baseline
    // snippet used before per-type schemas existed. Supplying real
    // schemas lets the agent (and simulate_workflow_run) catch missing
    // fields like `ssh.command` at plan time instead of at run time.
    //
    // The trailing fields exist for the one baseline that carries a body of its
    // own. Every handler seeded here is configured entirely from the node, so
    // Code stayed null for years; a `python_snippet` baseline is the exception,
    // and it needs the three columns that only mean something with a script
    // behind them (ScriptLanguage, LogicDiagramMermaid, NetworkEnabled).
    private sealed record SnippetTemplate(
        string Name,
        string Type,
        string Description,
        string TargetMode,
        int TimeoutSeconds,
        string? InputSchemaJson = null,
        string? OutputSchemaJson = null,
        string? Code = null,
        string? ScriptLanguage = null,
        string? LogicDiagramMermaid = null,
        bool NetworkEnabled = false,
        bool? ChangesState = null);

    private const string GenericObjectSchema = "{\"type\":\"object\"}";

    // Schemas for git. The handler dispatches on `operation`; each
    // operation needs a different mix of fields. We declare them as a
    // single oneOf so simulate_workflow_run + the agent know which
    // shape is required for which op without us emitting five
    // separate snippet types.
    private const string GitInputSchema = """
        {
          "type": "object",
          "required": ["operation", "repository_id"],
          "properties": {
            "operation":      { "type": "string", "enum": ["read_file", "write_file", "commit", "pull", "push"] },
            "repository_id":  { "type": "string", "description": "UUID of a registered Git repository (see /integrations/git)." },
            "path":           { "type": "string", "description": "Required for read_file and write_file." },
            "ref":            { "type": "string", "description": "Branch / tag / SHA. read_file only. Defaults to HEAD." },
            "content":        { "type": "string", "description": "Required for write_file. Templates expand before invocation." },
            "commit_message": { "type": "string", "description": "Required for write_file and commit." },
            "branch":         { "type": "string", "description": "Branch to operate on. write_file/commit/pull/push." },
            "push":           { "type": "boolean", "description": "write_file/commit only — push to origin after committing." },
            "paths":          { "type": "array", "items": { "type": "string" }, "description": "commit only — restrict staging to these paths. Omit to stage all." },
            "author_name":    { "type": "string" },
            "author_email":   { "type": "string" }
          }
        }
        """;
    private const string GitOutputSchema = """
        {
          "type": "object",
          "properties": {
            "ok":         { "type": "boolean" },
            "commit_sha": { "type": ["string", "null"] },
            "branch":     { "type": ["string", "null"] },
            "message":    { "type": "string" },
            "path":       { "type": "string" },
            "ref":        { "type": "string" },
            "content":    { "type": "string", "description": "read_file only." },
            "size":       { "type": "integer" },
            "is_binary":  { "type": "boolean" }
          }
        }
        """;

    // Schemas for ssh. Kept as constants so the admin UI and the agent
    // see the same contract the handler enforces at runtime.
    //
    // Backed by Netmiko (Python) as of the refactor. New optional inputs:
    //   - device_type       → Netmiko identifier (cisco_ios, juniper_junos,
    //                         arista_eos, nokia_sros, …). Defaults to
    //                         Device.Platform, then "generic".
    //   - enable_secret     → password for Cisco/Arista `enable` mode.
    //                         When set, the runner calls `conn.enable()`
    //                         right after connect.
    //   - use_structured    → triggers Netmiko's use_textfsm=True so
    //                         supported commands return parsed lists
    //                         (under `results[i].parsed`).
    //
    // Auth method + host-key pinning stay on Credential / Device rows
    // (NOT on config_overrides) — putting security knobs on the workflow
    // node would let a node override policy.
    private const string SshInputSchema = """
        {
          "type": "object",
          "x-order": ["command", "commands", "device_type", "host", "port", "timeout_seconds", "credential_id", "username", "password", "private_key", "key_passphrase", "enable_secret", "stop_on_error", "use_structured"],
          "oneOf": [
            { "required": ["command"] },
            { "required": ["commands"] }
          ],
          "additionalProperties": true,
          "properties": {
            "command":          { "type": "string", "minLength": 1, "description": "Single command to run. Mutually exclusive with `commands`. Validated at workflow create/update time against the vendor_commands catalog when `device_type` resolves; unknown commands surface as warnings (never block). Use the `validate_ssh_commands` AI tool to check candidates before committing." },
            "commands":         { "type": "array", "items": { "type": "string", "minLength": 1 }, "minItems": 1, "description": "Batch of commands to run on the SAME Netmiko session. Mutually exclusive with `command`. Same plan-time validation as `command` — each entry is checked against the vendor_commands catalog for the resolved device_type." },
            "stop_on_error":    { "type": "boolean", "default": true, "description": "When true (default), the first failing command aborts the remaining commands in the batch." },
            "host":             { "type": "string", "description": "Override the device's IP. Omit to let the handler resolve host from the target Device." },
            "port":             { "type": "integer", "minimum": 1, "maximum": 65535, "default": 22 },
            "timeout_seconds":  { "type": "integer", "minimum": 5, "maximum": 300, "default": 30 },
            "credential_id":    { "type": "string", "format": "uuid", "description": "Override the device's credential with a stored Credential (encrypted at rest). Omit to use device.CredentialId. Overridden by inline `username` when present." },
            "username":         { "type": "string", "description": "Inline (custom) credential override. When set, these per-run credentials take precedence over `credential_id` and the device's credential — no stored Credential row is needed. Security: inline secrets are NOT encrypted at rest; they persist in the workflow definition (if placed in config_overrides) and in the run/step input payload. Prefer a stored Credential for committed workflows; use inline for ad-hoc or run-time-injected credentials." },
            "password":         { "type": "string", "description": "Password for inline auth; used only when `username` is set. Provide `password` or `private_key`." },
            "private_key":      { "type": "string", "description": "PEM private key for inline key-based auth; used only when `username` is set. Alternative to `password`." },
            "key_passphrase":   { "type": "string", "description": "Optional passphrase protecting the inline `private_key`." },
            "device_type":      { "type": "string", "description": "Netmiko device_type (cisco_ios, juniper_junos, arista_eos, nokia_srl, huawei, linux, generic, …). Defaults to Device.Platform; final fallback is 'generic' (no vendor-specific prompt/paging handling)." },
            "enable_secret":    { "type": "string", "description": "Secret for `enable` mode on Cisco/Arista. The runner calls conn.enable() after connect when set." },
            "use_structured":   { "type": "boolean", "default": false, "description": "Enable Netmiko's use_textfsm=True. When a TextFSM template matches, the parsed list lands in results[i].parsed (raw string still in results[i].output)." }
          }
        }
        """;

    private const string SshOutputSchema = """
        {
          "type": "object",
          "properties": {
            "exit_code": { "type": "integer", "description": "Synthesized: 0 when every command succeeded, 1 otherwise. Back-compat for templates written before the Netmiko refactor — new templates should prefer results[i].ok." },
            "stdout":    { "type": "string", "description": "Output of the last command that actually ran." },
            "stderr":    { "type": "string", "description": "Error of the last command, empty on success." },
            "host_key_fingerprint": { "type": "string", "description": "SHA256 fingerprint observed at connect time (format 'SHA256:<base64-no-padding>'). Paste into device.ExpectedSshHostKeyFingerprint to pin." },
            "device_type": { "type": "string", "description": "Netmiko device_type the runner used (echoes the input or the resolved fallback)." },
            "results": {
              "type": "array",
              "description": "One entry per command in order, including commands blocked by policy (marked blocked_by_policy=true).",
              "items": {
                "type": "object",
                "properties": {
                  "command":     { "type": "string" },
                  "output":      { "type": "string", "description": "Raw command output. Empty when use_structured=true and a TextFSM template matched (see `parsed`)." },
                  "parsed":      { "description": "Structured result when use_structured=true and Netmiko matched a TextFSM template. Null otherwise." },
                  "elapsed_ms":  { "type": "integer" },
                  "ok":          { "type": "boolean" },
                  "error":       { "type": ["string", "null"] },
                  "blocked_by_policy": { "type": "boolean" },
                  "policy_name": { "type": ["string", "null"] }
                }
              }
            }
          }
        }
        """;

    // Schemas for the native slack_message snippet. channel + text are the
    // node's config_overrides; the bot token comes from deployment config.
    private const string SlackInputSchema = """
        {
          "type": "object",
          "required": ["channel", "text"],
          "x-order": ["channel", "text", "thread_ts"],
          "properties": {
            "channel":   { "type": "string", "description": "Target channel: \"#name\" or a channel ID (C0123…). The bot must be a member." },
            "text":      { "type": "string", "description": "Message body (Slack mrkdwn). Templates like {{ steps.X.output.field }} expand before send." },
            "thread_ts": { "type": "string", "description": "Optional. Parent message `ts` to reply in-thread." }
          }
        }
        """;
    private const string SlackOutputSchema = """
        {
          "type": "object",
          "properties": {
            "ok":          { "type": "boolean", "description": "TRUE only when Slack accepted the message. Slack returns HTTP 200 even on failure — branch on this, not the HTTP status." },
            "channel":     { "type": "string" },
            "ts":          { "type": ["string", "null"], "description": "Timestamp id of the posted message (use as thread_ts to reply)." },
            "error":       { "type": ["string", "null"], "description": "Slack error code when ok=false (channel_not_found, not_in_channel, invalid_auth)." },
            "status_code": { "type": "integer" }
          }
        }
        """;

    // Schemas for the native email_send snippet. Everything except the
    // credential is a node config_override; host/port/TLS/password live on the
    // EmailChannel row selected by `channel_id` (or the default channel).
    private const string EmailSendInputSchema = """
        {
          "type": "object",
          "required": ["to", "subject"],
          "x-order": ["channel_id", "to", "cc", "bcc", "subject", "body", "html", "from_address", "from_name", "reply_to", "attachments"],
          "properties": {
            "channel_id":   { "type": "string", "description": "Optional. The EmailChannel (SMTP relay) to send through — a UUID from /email. Omit to use the deployment's default channel." },
            "to":           { "description": "Recipient(s): one address, a comma-separated string, or an array of addresses.", "type": ["string", "array"], "items": { "type": "string" } },
            "cc":           { "description": "Optional carbon-copy recipient(s), same shape as `to`.", "type": ["string", "array"], "items": { "type": "string" } },
            "bcc":          { "description": "Optional blind-copy recipient(s), same shape as `to`.", "type": ["string", "array"], "items": { "type": "string" } },
            "subject":      { "type": "string", "description": "Subject line. Templates like {{ steps.X.output.field }} expand before send." },
            "body":         { "type": "string", "format": "textarea", "description": "Plain-text body. Supply `body`, `html`, or both — at least one is required." },
            "html":         { "type": "string", "format": "textarea", "description": "HTML body. When supplied alone, a plain-text alternative is generated automatically." },
            "from_address": { "type": "string", "description": "Optional override of the channel's sender. Most providers reject a From they do not own." },
            "from_name":    { "type": "string", "description": "Optional display name for the sender." },
            "reply_to":     { "type": "string", "description": "Optional Reply-To address." },
            "attachments":  {
              "type": "array",
              "description": "Optional attachments. Feed content_base64 from an upstream `report` step to email a generated PDF/XLSX.",
              "items": {
                "type": "object",
                "required": ["file_name", "content_base64"],
                "properties": {
                  "file_name":      { "type": "string" },
                  "content_base64": { "type": "string", "description": "Base64-encoded file content." },
                  "content_type":   { "type": "string", "description": "MIME type; defaults to application/octet-stream." }
                }
              }
            }
          }
        }
        """;
    private const string EmailSendOutputSchema = """
        {
          "type": "object",
          "properties": {
            "ok":           { "type": "boolean", "description": "TRUE only when the SMTP server accepted the message — branch on this." },
            "channel_id":   { "type": "string" },
            "channel_name": { "type": "string" },
            "provider":     { "type": "string", "description": "gmail | outlook365 | sendgrid | ses | mailgun | smtp." },
            "message_id":   { "type": ["string", "null"], "description": "RFC 5322 Message-ID of the sent message." },
            "recipients":   { "type": "integer", "description": "Total to + cc + bcc addresses." },
            "elapsed_ms":   { "type": "integer" },
            "error":        { "type": ["string", "null"], "description": "SMTP failure reason when ok=false (auth rejected, TLS handshake, unreachable relay)." }
          }
        }
        """;

    private const string McpCallInputSchema = """
        {
          "type": "object",
          "required": ["mcp_server_id", "tool_name"],
          "x-order": ["mcp_server_id", "tool_name", "arguments"],
          "properties": {
            "mcp_server_id": { "type": "string", "description": "The registered MCP server's id (a UUID). Browse servers/tools in the builder's MCP section." },
            "tool_name":     { "type": "string", "description": "The exact tool name on that server." },
            "arguments":     { "type": "object", "description": "Arguments matching the tool's input schema. Templates like {{ steps.X.output.field }} expand before the call." }
          }
        }
        """;
    private const string McpCallOutputSchema = """
        {
          "type": "object",
          "properties": {
            "content":    { "type": "string", "description": "The tool's text content (MCP content blocks concatenated)." },
            "structured": { "description": "Optional structured payload the tool returned." },
            "is_error":   { "type": "boolean", "description": "TRUE when the tool reported an error — branch on this." }
          }
        }
        """;

    // Schemas for the native ping snippet. The handler reads
    // host/count/timeout_ms from the merged payload; `host` is optional and
    // falls back to the step's target Device.IpAddress when omitted, so a
    // ping-on-inventory node needs no config at all.
    private const string PingInputSchema = """
        {
          "type": "object",
          "x-order": ["host", "count", "timeout_ms"],
          "properties": {
            "host":       { "type": "string", "description": "Target IP or hostname. Omit to ping the step's target Device (Device.IpAddress). A CIDR mask (…/16) is stripped automatically." },
            "count":      { "type": "integer", "minimum": 1, "maximum": 20, "default": 4, "description": "Number of ICMP echo requests to send (clamped 1-20)." },
            "timeout_ms": { "type": "integer", "minimum": 100, "maximum": 30000, "default": 3000, "description": "Per-request timeout in milliseconds (clamped 100-30000)." }
          }
        }
        """;
    private const string PingOutputSchema = """
        {
          "type": "object",
          "properties": {
            "success":          { "type": "boolean", "description": "True when at least one reply was received." },
            "rtt_avg_ms":       { "type": "number", "description": "Average round-trip time over successful replies (0 when none)." },
            "packets_sent":     { "type": "integer" },
            "packets_received": { "type": "integer" },
            "raw_output":       { "type": "string", "description": "Per-sequence log lines (seq / status / rtt)." }
          }
        }
        """;

    // Schemas for the native rest_call snippet. `url` is the only hard
    // requirement; `method` defaults to GET. `headers` is a free-form
    // string→value map and `body` is sent as application/json for the verbs
    // that carry one. For a registered Integration use integration_action
    // instead of a raw URL (the handler rejects operationId-looking URLs).
    private const string RestCallInputSchema = """
        {
          "type": "object",
          "required": ["url"],
          "x-order": ["url", "method", "headers", "body"],
          "properties": {
            "url":     { "type": "string", "description": "Absolute http/https URL (e.g. https://api.example.com/v1/things). Templates expand before the call. For a registered Integration use the integration_action snippet instead of a raw URL." },
            "method":  { "type": "string", "enum": ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], "default": "GET", "description": "HTTP verb." },
            "headers": { "type": "object", "description": "Request header name → value map (values must be strings), e.g. {\"Authorization\": \"Bearer …\"}." },
            "body":    { "type": "string", "format": "textarea", "description": "Request body, sent as application/json for POST/PUT/PATCH. A JSON string or plain text; templates expand before send." }
          }
        }
        """;
    private const string RestCallOutputSchema = """
        {
          "type": "object",
          "properties": {
            "status_code": { "type": "integer", "description": "HTTP status of the response. success = 2xx." },
            "body":        { "type": "string", "description": "Raw response body." },
            "headers":     { "type": "object", "description": "Response header name → value map (comma-joined when a header repeats)." }
          }
        }
        """;

    // Schemas for the native report snippet. Both `format` and `document`
    // are required — the handler refuses to guess a format. `document`
    // matches the rich ReportDocument tree, so we surface it as a raw JSON
    // object rather than exploding every nested field into the form; it's
    // normally assembled from upstream output via templates.
    private const string ReportInputSchema = """
        {
          "type": "object",
          "required": ["format", "document"],
          "x-order": ["format", "document"],
          "properties": {
            "format":   { "type": "string", "enum": ["html", "csv", "xlsx", "pdf"], "description": "Output file format." },
            "document": { "type": "object", "description": "Report document (title, sections, tables…) matching the ReportDocument schema. Usually assembled from upstream output via {{ steps.X.output.… }} templates." }
          }
        }
        """;
    private const string ReportOutputSchema = """
        {
          "type": "object",
          "properties": {
            "report_artifact_id": { "type": "string", "description": "UUID of the stored artifact (see /admin/reports)." },
            "filename":           { "type": "string" },
            "content_type":       { "type": "string" },
            "format":             { "type": "string" },
            "size_bytes":         { "type": "integer" },
            "sha256":             { "type": "string" },
            "download_url":       { "type": "string", "description": "Relative API path to download the file." },
            "base64":             { "type": "string", "description": "Raw file bytes, base64-encoded — plug into a downstream email/upload node via {{ steps.X.output.base64 }}." }
          }
        }
        """;

    // ── The paramiko primitive ──────────────────────────────────────────────
    //
    // The `ssh` step is netmiko with vendor awareness, host-key pinning and
    // TextFSM parsing, and it is the right answer for "send commands, read
    // output". What it cannot do is hold a CONVERSATION: a password change that
    // asks twice, a "[confirm y/n]", a commit-confirm. Before this baseline
    // existed the only route to one was for an admin to hand-write a
    // network-enabled snippet from scratch, which meant every deployment grew
    // its own half-correct expect loop.
    //
    // So this is seeded as the primitive underneath both shapes — exec and
    // interactive — with the two things a hand-written one keeps getting wrong
    // done once: the agent/`~/.ssh` keys of the worker are refused (a workflow
    // must not reach a device on the HOST's credentials), and a step marked
    // `secret` keeps its payload out of the stored run artifact.
    private const string ParamikoInputSchema = """
        {
          "type": "object",
          "x-order": ["mode", "commands", "steps", "host", "port", "username", "password", "private_key", "key_passphrase", "shell_prompt", "stop_on_error", "host_key_policy"],
          "required": ["username"],
          "additionalProperties": true,
          "properties": {
            "mode":            { "type": "string", "enum": ["exec", "shell"], "default": "exec", "description": "'exec' runs each command on its own channel and reports exit_status per command — servers and Linux-like NOSes; many classic CLIs refuse exec entirely. 'shell' opens ONE interactive shell driven by `steps`, which is the only mode that can answer a prompt." },
            "command":         { "type": "string", "description": "exec mode — shorthand for a single-entry `commands`." },
            "commands":        { "type": "array", "items": { "type": "string" }, "description": "exec mode — one channel per entry, run in order." },
            "steps":           { "type": "array", "description": "shell mode — the expect script, run in order.", "items": { "type": "object", "required": ["send"], "properties": {
                                   "send":            { "type": "string", "description": "Text written to the channel." },
                                   "expect":          { "type": "string", "description": "Regex to wait for after sending. Defaults to `shell_prompt`." },
                                   "terminator":      { "type": "string", "default": "\n", "description": "Appended to `send`. Set \"\" to send a bare keystroke." },
                                   "timeout_seconds": { "type": "integer", "description": "Overrides `read_timeout_seconds` for this step." },
                                   "secret":          { "type": "boolean", "default": false, "description": "Replaces this step's `send` with *** in the output. Set it on anything carrying a password — the step output is a stored run artifact." },
                                   "redact_output":   { "type": "boolean", "default": false, "description": "With `secret`, also drops what the device echoed back for this step." } } } },
            "host":            { "type": "string", "description": "Target address. Omit on a per_device step to use the device's own IP." },
            "port":            { "type": "integer", "minimum": 1, "maximum": 65535, "default": 22 },
            "username":        { "type": "string", "description": "Pass \"${secret:credential:<credential_id>:username}\" — the reference resolves before the script runs, so no plaintext is stored on the node." },
            "password":        { "type": "string", "description": "\"${secret:credential:<credential_id>:password}\". Provide this or `private_key`." },
            "private_key":     { "type": "string", "description": "PEM key: \"${secret:credential:<credential_id>:private_key}\". ed25519 / ecdsa / rsa / dsa are all tried." },
            "key_passphrase":  { "type": "string", "description": "\"${secret:credential:<credential_id>:passphrase}\"." },
            "shell_prompt":    { "type": "string", "default": "[>#$%]\\s*$", "description": "shell mode — the regex a step waits for when it declares no `expect` of its own." },
            "connect_timeout_seconds": { "type": "integer", "default": 15 },
            "command_timeout_seconds": { "type": "integer", "default": 30, "description": "exec mode — per command." },
            "read_timeout_seconds":    { "type": "integer", "default": 15, "description": "shell mode — how long one step waits for its `expect`." },
            "idle_seconds":    { "type": "number", "default": 1.0, "description": "shell mode — how long the channel must stay quiet before a step with no `expect` is considered finished." },
            "stop_on_error":   { "type": "boolean", "default": true, "description": "Stop at the first non-zero exit_status (exec) or unmatched `expect` (shell)." },
            "strip_ansi":      { "type": "boolean", "default": true, "description": "Strip ANSI/CSI/OSC and C0 noise before matching and before returning. Leaving it in turns an `expect` into a coin flip." },
            "host_key_policy": { "type": "string", "enum": ["auto_add", "reject"], "default": "auto_add", "description": "'auto_add' is trust-on-first-use: the host key is NOT verified. 'reject' verifies against the worker's known_hosts and fails on an unknown host. The built-in `ssh` step pins properly and is the better default for routine work." }
          }
        }
        """;

    private const string ParamikoOutputSchema = """
        {
          "type": "object",
          "properties": {
            "ok":         { "type": "boolean", "description": "Every command exited 0 (exec), or every step matched its `expect` (shell). False is a result to branch on, not a step failure — only connect/auth errors fail the step." },
            "mode":       { "type": "string", "enum": ["exec", "shell"] },
            "host":       { "type": "string" },
            "port":       { "type": "integer" },
            "results":    { "type": "array", "description": "exec mode.", "items": { "type": "object", "properties": {
                              "command":     { "type": "string" },
                              "stdout":      { "type": "string" },
                              "stderr":      { "type": "string" },
                              "exit_status": { "type": "integer" },
                              "ok":          { "type": "boolean" } } } },
            "failed":     { "type": "array", "items": { "type": "string" }, "description": "exec mode — the commands that exited non-zero." },
            "steps":      { "type": "array", "description": "shell mode.", "items": { "type": "object", "properties": {
                              "send":    { "type": "string", "description": "*** when the step declared `secret`." },
                              "expect":  { "type": "string" },
                              "matched": { "type": "boolean" },
                              "output":  { "type": "string" } } } },
            "transcript": { "type": "string", "description": "shell mode — the whole session, banner included." },
            "unmatched":  { "type": "array", "items": { "type": "string" }, "description": "shell mode — the `expect` patterns that never showed up." }
          }
        }
        """;

    private const string ParamikoDiagram = """
        flowchart TD
            in([config_overrides: host, credential, mode]) --> conn[SSHClient.connect - agent and ~/.ssh keys refused]
            conn -->|auth or connect fails| fail([step FAILS])
            conn --> mode{mode}
            mode -->|exec| ex[exec_command per command]
            ex --> exr([results: stdout, stderr, exit_status per command])
            mode -->|shell| sh[invoke_shell, read the banner]
            sh --> loop[per step: send, then read until expect]
            loop -->|matched| loop
            loop -->|no match and stop_on_error| stop[ok = false]
            loop --> shr([transcript + matched per step])
            stop --> shr
            exr --> close[close the connection]
            shr --> close
            close --> out([ok, mode, host, results or transcript])
        """;

    // No `"""` anywhere in this body: it is embedded in a C# raw string literal
    // and a triple quote would close it. Comments are `#` only, deliberately.
    internal const string ParamikoCode = """
        # SSH primitive (paramiko) — one connection, two modes.
        #
        #   mode="exec"  (default)  each entry of `commands` runs on its own channel
        #                           via exec_command, so you get stdout, stderr and
        #                           exit_status per command. Servers and Linux-like
        #                           network OSes; many classic CLIs refuse exec.
        #   mode="shell"            ONE interactive shell driven by `steps`, each
        #                           {send, expect}. This is the mode for an exchange
        #                           exec cannot hold: a password change that asks
        #                           twice, a "[confirm y/n]", a commit-confirm.
        #
        # Deliberately NOT device-aware. This is the primitive the vendor-shaped
        # steps are built out of, not a replacement for them — when the built-in
        # `ssh` step covers the job, use that instead: it pins host keys, handles
        # paging and parses output. Reach for this one when the INTERACTION is the
        # problem.

        import io
        import re
        import time

        import paramiko
        from flowweaver_runtime import get_input, set_output

        # ANSI/CSI/OSC and the C0 control bytes a live TTY sprays into a shell
        # channel. Left in, they turn an `expect` regex into a coin flip.
        #
        # Kept as a PATTERN STRING, not a compiled object, and every regex below
        # goes through the module-level re.* functions for the same reason: the
        # import guard blocks the name `compile` wherever it appears, attribute
        # access included, so `re.compile(...)` is refused before the sandbox is
        # even built. re's own internal cache makes this free anyway — do not
        # "optimise" it back into a compiled pattern.
        _NOISE = (
            r"\x1b\[[0-9;?]*[ -/]*[@-~]"
            r"|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)"
            r"|\x1b[@-Z\\-_]"
            r"|[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")


        def _clean(text, strip):
            return re.sub(_NOISE, "", text) if strip else text


        def _load_key(pem, passphrase):
            # paramiko has no "work out the type" loader, so try each class and keep
            # the first that parses. The order is cheapest-first, not a preference.
            last = None
            for cls in (paramiko.Ed25519Key, paramiko.ECDSAKey, paramiko.RSAKey, paramiko.DSSKey):
                try:
                    return cls.from_private_key(io.StringIO(pem), password=passphrase or None)
                except Exception as err:
                    last = err
            raise ValueError("private_key did not parse as ed25519, ecdsa, rsa or dsa: %s" % last)


        def _config(inp):
            device = inp.get("device") or {}
            host = inp.get("host") or device.get("ip") or device.get("name")
            if not host:
                raise ValueError("no host: pass `host`, or run this step against a device")

            username = inp.get("username")
            password = inp.get("password")
            private_key = inp.get("private_key")
            if not username:
                raise ValueError(
                    "no username: pass `username`, e.g. "
                    "${secret:credential:<credential_id>:username} — the reference is "
                    "resolved before this script runs, so no plaintext is stored on the node")
            if not password and not private_key:
                raise ValueError("no credential: pass `password` or `private_key`")

            mode = str(inp.get("mode") or "exec").strip().lower()
            if mode not in ("exec", "shell"):
                raise ValueError("mode must be 'exec' or 'shell', not %r" % mode)

            commands = inp.get("commands")
            if not commands and inp.get("command"):
                commands = [inp["command"]]
            steps = inp.get("steps") or []

            if mode == "exec" and not commands:
                raise ValueError("mode='exec' needs `command` or `commands`")
            if mode == "shell" and not steps:
                raise ValueError("mode='shell' needs `steps`, each {send, expect}")

            return {
                "host": host,
                "port": int(inp.get("port") or 22),
                "username": username,
                "password": password,
                "private_key": private_key,
                "key_passphrase": inp.get("key_passphrase"),
                "mode": mode,
                "commands": list(commands or []),
                "steps": list(steps),
                "connect_timeout": int(inp.get("connect_timeout_seconds") or 15),
                "command_timeout": int(inp.get("command_timeout_seconds") or 30),
                "read_timeout": int(inp.get("read_timeout_seconds") or 15),
                "idle": float(inp.get("idle_seconds") or 1.0),
                "prompt": inp.get("shell_prompt") or r"[>#$%]\s*$",
                "stop_on_error": bool(inp.get("stop_on_error", True)),
                "strip_ansi": bool(inp.get("strip_ansi", True)),
                "host_key_policy": str(inp.get("host_key_policy") or "auto_add").lower(),
            }


        def _connect(cfg):
            client = paramiko.SSHClient()
            if cfg["host_key_policy"] == "reject":
                client.load_system_host_keys()
                client.set_missing_host_key_policy(paramiko.RejectPolicy())
            else:
                # Trust on first use, said out loud rather than left to look like
                # pinning: under the default policy this primitive does NOT verify
                # the host key. The built-in `ssh` step does.
                client.set_missing_host_key_policy(paramiko.AutoAddPolicy())

            kwargs = {
                "hostname": cfg["host"],
                "port": cfg["port"],
                "username": cfg["username"],
                "timeout": cfg["connect_timeout"],
                "banner_timeout": cfg["connect_timeout"],
                "auth_timeout": cfg["connect_timeout"],
                # The WORKER's ssh-agent and ~/.ssh keys are not this step's
                # credentials. Left on, paramiko tries them silently and a workflow
                # reaches a device nobody ever gave it a credential for.
                "allow_agent": False,
                "look_for_keys": False,
            }
            if cfg["private_key"]:
                kwargs["pkey"] = _load_key(cfg["private_key"], cfg["key_passphrase"])
            else:
                kwargs["password"] = cfg["password"]

            client.connect(**kwargs)
            return client


        def _run_commands(client, cfg):
            results = []
            ok = True
            for command in cfg["commands"]:
                stdin, stdout, stderr = client.exec_command(command, timeout=cfg["command_timeout"])
                stdin.close()
                out = stdout.read().decode("utf-8", "replace")
                err = stderr.read().decode("utf-8", "replace")
                status = stdout.channel.recv_exit_status()
                results.append({
                    "command": command,
                    "stdout": _clean(out, cfg["strip_ansi"]),
                    "stderr": _clean(err, cfg["strip_ansi"]),
                    "exit_status": status,
                    "ok": status == 0,
                })
                if status != 0:
                    ok = False
                    if cfg["stop_on_error"]:
                        break
            return {
                "mode": "exec",
                "ok": ok,
                "results": results,
                "failed": [r["command"] for r in results if not r["ok"]],
            }


        def _read(chan, pattern, timeout, idle, strip):
            # Returns (text, matched). WITH a pattern, matched means it showed up
            # before the deadline. WITHOUT one the only stop condition is silence: a
            # device sitting at its prompt with nothing left to say looks exactly
            # like one that never started, so `idle` is what decides it is done.
            deadline = time.monotonic() + timeout
            buf = ""
            last = time.monotonic()
            while time.monotonic() < deadline:
                chan.settimeout(0.5)
                try:
                    chunk = chan.recv(65535)
                except TimeoutError:
                    # socket.timeout IS TimeoutError from Python 3.10 on, so this
                    # catches paramiko's read timeout without importing socket.
                    chunk = b""
                except paramiko.SSHException:
                    break
                if chunk:
                    buf += chunk.decode("utf-8", "replace")
                    last = time.monotonic()
                    if pattern and re.search(pattern, _clean(buf, strip)):
                        return _clean(buf, strip), True
                    continue
                if chan.closed or chan.eof_received:
                    break
                if not pattern and buf and (time.monotonic() - last) >= idle:
                    return _clean(buf, strip), True
            return _clean(buf, strip), (not pattern) and bool(buf)


        def _run_shell(client, cfg):
            # Wide and tall on purpose: a narrow pty wraps lines mid-token and the
            # `expect` never matches what the operator typed into the node.
            chan = client.invoke_shell(width=511, height=1000)
            try:
                banner, _ = _read(
                    chan, cfg["prompt"], cfg["read_timeout"], cfg["idle"], cfg["strip_ansi"])
                transcript = banner
                performed = []
                ok = True
                for step in cfg["steps"]:
                    send = step.get("send", "")
                    expect = step.get("expect") or cfg["prompt"]
                    timeout = int(step.get("timeout_seconds") or cfg["read_timeout"])
                    terminator = step.get("terminator")
                    if terminator is None:
                        terminator = "\n"
                    chan.send((send + terminator).encode("utf-8"))
                    out, matched = _read(chan, expect, timeout, cfg["idle"], cfg["strip_ansi"])
                    transcript += out
                    secret = bool(step.get("secret"))
                    performed.append({
                        # A step marked `secret` keeps its payload out of the step
                        # output. The output is a STORED run artifact — a new
                        # password sent to a device must not come back as something
                        # anyone with read access can page through.
                        "send": "***" if secret else send,
                        "expect": expect,
                        "matched": matched,
                        "output": "***" if secret and step.get("redact_output") else out,
                    })
                    if not matched:
                        ok = False
                        if cfg["stop_on_error"]:
                            break
                return {
                    "mode": "shell",
                    "ok": ok,
                    "steps": performed,
                    "transcript": transcript,
                    "unmatched": [s["expect"] for s in performed if not s["matched"]],
                }
            finally:
                chan.close()


        _cfg = _config(get_input())
        _client = _connect(_cfg)
        try:
            _out = _run_commands(_client, _cfg) if _cfg["mode"] == "exec" else _run_shell(_client, _cfg)
        finally:
            _client.close()

        _out["host"] = _cfg["host"]
        _out["port"] = _cfg["port"]
        set_output(_out)
        """;

    private static readonly SnippetTemplate[] Templates =
    {
        new("ping", "ping",
            "ICMP ping against a target device. Use as the first step of a "
            + "reachability workflow.",
            "per_device", 10,
            PingInputSchema, PingOutputSchema),
        new("rest_call", "rest_call",
            "Generic HTTP call to an external URL. Configure method, url, "
            + "headers and body in the node's config_overrides.",
            "once", 30,
            RestCallInputSchema, RestCallOutputSchema),
        new("ssh", "ssh",
            "Run one or many commands on a target device over SSH, backed by "
            + "Netmiko (Python). Single mode: set `command`. Batch mode (same "
            + "session): set `commands`. `stop_on_error` (default true) aborts "
            + "on the first failure. Netmiko gives vendor-aware prompt + paging "
            + "handling — pass `device_type` (or set Device.Platform to a "
            + "Netmiko identifier like cisco_ios, juniper_junos, arista_eos, "
            + "nokia_srl) to get it. Default fallback is generic (no "
            + "vendor tricks). Host + credential come from the target Device "
            + "by default. Auth is driven by Credential.AuthMethod "
            + "('password' or 'key'); host-key pinning is driven by "
            + "Device.ExpectedSshHostKeyFingerprint; per-command policy by "
            + "policy rules with `when.ssh_command_regex`. Output carries a "
            + "`results[]` array (each with ok/output/parsed/elapsed_ms) and "
            + "keeps top-level exit_code/stdout/stderr pointing at the last "
            + "command for back-compat with pre-Netmiko templates.",
            "per_device", 30,
            SshInputSchema, SshOutputSchema),
        new("transform", "transform",
            "JMESPath transform over the step's input. The expression lives "
            + "in the snippet's Code field.",
            "once", 5),
        new("report", "report",
            "Generate a downloadable report (html/csv/xlsx/pdf) from a "
            + "structured document. Set `format` + `document` in the "
            + "node's config_overrides; downstream nodes read "
            + "{{ steps.X.output.base64 }} and {{ steps.X.output.filename }} "
            + "to email or upload it.",
            "once", 30,
            ReportInputSchema, ReportOutputSchema),
        new("integration_action", "integration_action",
            "Call a registered Integration action (e.g. AWX launch, "
            + "NetBox query, email send). Set `integration_id` and "
            + "`action_id` in the node's config_overrides along with any "
            + "body/params the action needs. Look up available actions "
            + "with fw_integrations:list_palette_actions.",
            "once", 30),
        new("git", "git",
            "Read, write, commit, pull, or push files in a registered Git "
            + "repository (see /integrations/git). Set `operation` + "
            + "`repository_id` in the node's config_overrides; supply "
            + "`path` + `content` + `commit_message` for write_file, "
            + "`branch` + `push` to control where it lands. Repository "
            + "auth (HTTPS+PAT or SSH key) lives on the registered repo, "
            + "not on the node. Use `target_mode='once'` — Git ops are "
            + "repo-scoped, not device-scoped.",
            "once", 60,
            GitInputSchema, GitOutputSchema),
        new("slack_message", "slack_message",
            "Send a Slack message / notification / alert. Posts to a Slack "
            + "channel via chat.postMessage. Use this whenever a workflow should "
            + "notify a Slack channel. Set "
            + "`channel` (\"#alerts\" or a channel ID) and `text` in the node's "
            + "config_overrides; optional `thread_ts` to reply in a thread. The "
            + "bot token is a deployment secret (Slack:BotToken / SLACK_BOT_TOKEN) "
            + "and the bot must be a member of the channel. Slack returns ok=false "
            + "(not an HTTP error) on failure — branch on "
            + "{{ steps.X.output.ok }}. Use target_mode='once' (not device-scoped).",
            "once", 30,
            SlackInputSchema, SlackOutputSchema),
        new("email_send", "email_send",
            "Send an email / e-mail notification / report by mail. Delivers "
            + "through a configured SMTP relay (Gmail, Microsoft 365, SendGrid, "
            + "Amazon SES, Mailgun or a custom server — see /email). Use this "
            + "whenever a workflow should email someone. Set `to` and `subject` "
            + "in the node's config_overrides plus `body` (plain text) and/or "
            + "`html`; optional `cc`, `bcc`, `reply_to` and `attachments` "
            + "(base64 — feed it from an upstream `report` step). Omit "
            + "`channel_id` to use the default channel. Host, port, TLS and the "
            + "password live on the channel, never on the node. The send is "
            + "NOT reversible. Branch on {{ steps.X.output.ok }}. Use "
            + "target_mode='once' (not device-scoped).",
            "once", 60,
            EmailSendInputSchema, EmailSendOutputSchema),
        new("mcp_call", "mcp_call",
            "Call a tool on a registered external MCP server. Set `mcp_server_id` "
            + "and `tool_name` in the node's config_overrides plus an `arguments` "
            + "object matching the tool's input schema. Browse servers/tools in the "
            + "builder's MCP section (or via list_mcp_servers / discover_mcp_tools). "
            + "Reads {{ steps.X.output.content }} downstream. Use target_mode='once' "
            + "(not device-scoped).",
            "once", 30,
            McpCallInputSchema, McpCallOutputSchema),
        new("SSH primitive (paramiko)", "python_snippet",
            "Raw SSH via paramiko, for an interaction the `ssh` step cannot "
            + "express. Two modes on one connection: `mode='exec'` (default) "
            + "runs each of `commands` on its own channel and reports "
            + "stdout/stderr/exit_status per command; `mode='shell'` opens ONE "
            + "interactive shell and drives it with `steps`, each {send, expect} "
            + "— that is the mode for a password change that prompts twice, a "
            + "\"[confirm y/n]\" or a commit-confirm. PREFER THE `ssh` STEP for "
            + "ordinary \"send commands, read output\": it is netmiko with "
            + "vendor-aware parsing, host-key pinning and policy checks, and this "
            + "one runs with the network sandbox lifted. Credentials are never "
            + "auto-injected — pass "
            + "\"${secret:credential:<credential_id>:username}\" and :password / "
            + ":private_key through config_overrides and they resolve before the "
            + "script runs. Mark any step carrying a secret with `secret: true` "
            + "so its payload stays out of the stored run output. `ok: false` is "
            + "a result to branch on; only connect/auth errors fail the step.",
            "per_device", 120,
            ParamikoInputSchema, ParamikoOutputSchema,
            Code: ParamikoCode,
            ScriptLanguage: "python",
            LogicDiagramMermaid: ParamikoDiagram,
            // The whole point of the snippet: paramiko, socket and the relaxed
            // sandbox are unreachable without it. Seeding it true ships a
            // network-capable snippet on a fresh install, which the API path
            // gates to admins — flip this to false if a deployment would rather
            // an admin turn it on deliberately, at the cost of a baseline that
            // fails its first run with "disallowed module 'paramiko'".
            NetworkEnabled: true,
            // Conservative, and the node overrides it. A snippet that can answer
            // a config prompt is presumed to change something; a node that knows
            // its own commands are read-only says so with
            // `config_overrides.changes: false`, which wins over this. Null would
            // be the honest "the author has not said" — but for THIS type it also
            // means every step fails until some node declares it, and a baseline
            // that cannot run out of the box teaches nothing.
            ChangesState: true),
    };

    public static async Task SeedAsync(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await SeedDefaultsAsync(db, logger);

    }

    private static async Task SeedDefaultsAsync(AppDbContext db, ILogger logger)
    {
        // Postgres advisory lock. Two concurrent backend boots (docker
        // compose scale=2, watch-reload overlap, rapid container
        // restarts) used to race: both would read `existingTypes` before
        // either committed, both would INSERT, and the DB ended up with
        // duplicate baseline snippets. The lock serializes seeders; the
        // second caller sees the first's inserts on its own read.
        //
        // Fixed advisory-lock key: two concurrent boots must not
        // double-insert the baseline snippets.
        const long lockKey = 0x5311_9E75_0000_0001;

        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock({0})", lockKey);

            // Deduplicate any pre-existing duplicates left behind by the
            // old racy seeder. Keep the oldest row per (Type, Name) pair
            // so workflow nodes that already reference it keep working.
            var existingBaseline = await db.Snippets
                .Where(s => s.IsActive
                    && Templates.Select(t => t.Type).Contains(s.Type)
                    && Templates.Select(t => t.Name).Contains(s.Name))
                .OrderBy(s => s.CreatedAt)
                .ToListAsync();

            var now = DateTime.UtcNow;
            var seenByType = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dedupedCount = 0;
            var refreshedCount = 0;
            foreach (var row in existingBaseline)
            {
                if (seenByType.Add(row.Type))
                {
                    // First (kept) row of this built-in type. Keep its contract
                    // schema in sync with the seed template so an existing
                    // install picks up new input fields (e.g. inline ssh
                    // credentials) in the node editor — the seed is otherwise
                    // insert-only and would never refresh a pre-existing
                    // built-in. Only the documented contract (Input/Output
                    // schema + Description) is synced; Code/TargetMode/timeouts
                    // and local edits to those are left untouched. No-op when
                    // already current.
                    var match = Templates.FirstOrDefault(t =>
                        string.Equals(t.Type, row.Type, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(t.Name, row.Name, StringComparison.OrdinalIgnoreCase));
                    if (match is not null && RefreshBuiltinContract(row, match, now))
                        refreshedCount++;
                    continue;
                }
                // Second+ row for the same type — soft-delete it.
                row.IsActive = false;
                row.UpdatedAt = now;
                dedupedCount++;
            }

            var keptTypes = new HashSet<string>(seenByType, StringComparer.OrdinalIgnoreCase);
            var inserted = 0;
            foreach (var tpl in Templates)
            {
                if (keptTypes.Contains(tpl.Type)) continue;

                db.Snippets.Add(new SnippetModel
                {
                    SnippetId = Guid.NewGuid(),
                    Name = tpl.Name,
                    Type = tpl.Type,
                    Description = tpl.Description,
                    TargetMode = tpl.TargetMode,
                    TimeoutSeconds = tpl.TimeoutSeconds,
                    MaxParallel = 10,
                    Verified = true,
                    Code = tpl.Code,
                    ScriptLanguage = tpl.ScriptLanguage,
                    LogicDiagramMermaid = tpl.LogicDiagramMermaid,
                    NetworkEnabled = tpl.NetworkEnabled,
                    ChangesState = tpl.ChangesState,
                    InputSchema = JsonDocument.Parse(tpl.InputSchemaJson ?? GenericObjectSchema).RootElement,
                    OutputSchema = JsonDocument.Parse(tpl.OutputSchemaJson ?? GenericObjectSchema).RootElement,
                    RetryPolicy = JsonDocument.Parse("{}").RootElement,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                inserted++;
            }

            if (inserted > 0 || dedupedCount > 0 || refreshedCount > 0)
            {
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                if (inserted > 0)
                    logger.LogInformation(
                        "Seeded {Count} default snippet(s)",
                        inserted);
                if (refreshedCount > 0)
                    logger.LogInformation(
                        "Refreshed {Count} built-in snippet contract(s)",
                        refreshedCount);
                if (dedupedCount > 0)
                    logger.LogWarning(
                        "Deactivated {Count} duplicate baseline snippet(s)",
                        dedupedCount);
            }
            else
            {
                await tx.CommitAsync();
            }
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    // Syncs a kept built-in snippet's documented contract to its seed template.
    // Returns true only when something actually changed, so an unchanged boot is
    // a no-op (no UpdatedAt churn). Postgres jsonb normalises object key order on
    // the way out, so compare semantically with JsonNode.DeepEquals — raw-text
    // comparison would report a spurious diff every boot.
    private static bool RefreshBuiltinContract(SnippetModel row, SnippetTemplate tpl, DateTime now)
    {
        var desiredInput = JsonDocument.Parse(tpl.InputSchemaJson ?? GenericObjectSchema).RootElement;
        var desiredOutput = JsonDocument.Parse(tpl.OutputSchemaJson ?? GenericObjectSchema).RootElement;
        var changed = false;

        if (!JsonEquals(row.InputSchema, desiredInput))
        {
            row.InputSchema = desiredInput;
            changed = true;
        }
        if (!JsonEquals(row.OutputSchema, desiredOutput))
        {
            row.OutputSchema = desiredOutput;
            changed = true;
        }
        if (!string.Equals(row.Description, tpl.Description, StringComparison.Ordinal))
        {
            row.Description = tpl.Description;
            changed = true;
        }
        if (changed) row.UpdatedAt = now;
        return changed;
    }

    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        var aUndef = a.ValueKind == JsonValueKind.Undefined;
        var bUndef = b.ValueKind == JsonValueKind.Undefined;
        if (aUndef || bUndef) return aUndef && bUndef;
        return JsonNode.DeepEquals(JsonNode.Parse(a.GetRawText()), JsonNode.Parse(b.GetRawText()));
    }
}
