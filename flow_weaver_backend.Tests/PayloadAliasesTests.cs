using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Git;
using flow_weaver_backend.Services.Identity;
using flow_weaver_backend.Services.Mcp;
using flow_weaver_backend.Services.Worker;
using flow_weaver_backend.Services.Worker.Handlers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using CredentialModel = flow_weaver_backend.Models.Credential;

namespace flow_weaver_backend.Tests;

// snippets/SPEC.md: one canonical key per thing, with the other product's
// spelling accepted as an alias. Getting this wrong does not break the import —
// it breaks the FIRST RUN, days later, on someone else's instance, with an
// error that says a required key is missing while the node plainly carries it.
// These are the cases that make a Nashira-authored workflow execute here.
public class PayloadAliasesTests
{
    private static SnippetRequest Req(string type, object input, Guid? deviceId = null, string? code = null)
        => new()
        {
            StepRunId = Guid.NewGuid(),
            WorkflowRunId = Guid.NewGuid(),
            NodeId = "n1",
            SnippetId = Guid.NewGuid(),
            SnippetType = type,
            InputPayload = JsonSerializer.SerializeToElement(input),
            DeviceId = deviceId,
            SnippetCode = code,
        };

    private static JsonElement Normalize(string type, string json)
        => PayloadAliases.Normalize(type, BundleTest.Json(json));

    // ── transform: `mapping` IS a JMESPath multiselect hash ───────────────

    [Fact]
    public void Transform_mapping_becomes_the_equivalent_multiselect_hash()
    {
        var result = Normalize("transform", """{"mapping":{"name":"device.name","ip":"device.primary_ip"}}""");

        Assert.Equal("{name: device.name, ip: device.primary_ip}", result.GetProperty("expression").GetString());
        Assert.False(result.TryGetProperty("mapping", out _));
    }

    [Fact]
    public void Transform_mapping_quotes_a_key_that_is_not_a_bare_identifier()
    {
        var result = Normalize("transform", """{"mapping":{"device-name":"a.b"}}""");

        Assert.Equal("{\"device-name\": a.b}", result.GetProperty("expression").GetString());
    }

    [Fact]
    public void Transform_a_declared_expression_wins_over_a_stray_mapping()
    {
        // A node carrying both is a FlowWeaver node with a leftover key; the
        // canonical spelling is what it means.
        var result = Normalize("transform", """{"expression":"items[?active]","mapping":{"x":"y"}}""");

        Assert.Equal("items[?active]", result.GetProperty("expression").GetString());
        Assert.False(result.TryGetProperty("mapping", out _));
    }

    [Fact]
    public async Task Transform_runs_a_mapping_end_to_end()
    {
        var h = new TransformHandler(NullLogger<TransformHandler>.Instance);

        var result = await h.ExecuteAsync(
            Req("transform", new
            {
                mapping = new { hostname = "device.name" },
                input = new { device = new { name = "core-sw-1" } },
            }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("core-sw-1", result.Output.GetProperty("hostname").GetString());
    }

    // ── integration_action ────────────────────────────────────────────────

    [Fact]
    public void Integration_action_path_and_query_params_take_their_canonical_names()
    {
        var result = Normalize(
            "integration_action",
            """{"integration":"netbox","action":"get_device","path_params":{"device_id":"7"},"query_params":{"brief":"1"}}""");

        Assert.Equal("7", result.GetProperty("params").GetProperty("device_id").GetString());
        Assert.Equal("1", result.GetProperty("query").GetProperty("brief").GetString());
        Assert.False(result.TryGetProperty("path_params", out _));
        Assert.False(result.TryGetProperty("query_params", out _));
        // Everything else is copied verbatim.
        Assert.Equal("netbox", result.GetProperty("integration").GetString());
    }

    // ── mcp_call ──────────────────────────────────────────────────────────

    [Fact]
    public void Mcp_tool_name_becomes_tool()
    {
        // `tool` is the CANONICAL key and `tool_name` the alias. This assertion
        // used to read the other way round, which is how the rename table came
        // to point backwards.
        var result = Normalize("mcp_call", """{"server":"netops","tool_name":"list_devices","arguments":{}}""");

        Assert.Equal("list_devices", result.GetProperty("tool").GetString());
        Assert.False(result.TryGetProperty("tool_name", out _));
    }

    [Fact]
    public void Mcp_a_declared_tool_wins_over_a_stray_tool_name()
    {
        // The one that matters. Written the other way round, a node carrying
        // both kept `tool_name` and DISCARDED `tool`: the step reported success
        // for a call it never made, having made a different one.
        var result = Normalize(
            "mcp_call", """{"server":"netops","tool":"list_devices","tool_name":"delete_device"}""");

        Assert.Equal("list_devices", result.GetProperty("tool").GetString());
        Assert.False(result.TryGetProperty("tool_name", out _));
    }

    [Fact]
    public async Task Mcp_handler_calls_the_canonical_tool_when_a_node_carries_both()
    {
        var exec = new RecordingMcpExecutor();
        var h = new McpCallHandler(exec, NullLogger<McpCallHandler>.Instance);

        var result = await h.ExecuteAsync(
            Req("mcp_call", new
            {
                mcp_server_id = Guid.NewGuid().ToString(),
                tool = "list_devices",
                tool_name = "delete_device",
            }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("list_devices", exec.LastTool);
    }

    [Fact]
    public async Task Mcp_handler_accepts_the_portable_tool_key()
    {
        var server = Guid.NewGuid();
        var exec = new RecordingMcpExecutor();
        var h = new McpCallHandler(exec, NullLogger<McpCallHandler>.Instance);

        var result = await h.ExecuteAsync(
            Req("mcp_call", new { mcp_server_id = server.ToString(), tool = "list_devices", arguments = new { } }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("list_devices", exec.LastTool);
        Assert.Equal(server, exec.LastServer);
    }

    // ── ssh ───────────────────────────────────────────────────────────────

    [Fact]
    public void Ssh_structured_and_enable_take_their_canonical_names()
    {
        var result = Normalize("ssh", """{"commands":["show version"],"structured":true,"enable":"${secret:store:x:enable}"}""");

        Assert.Equal(JsonValueKind.True, result.GetProperty("use_structured").ValueKind);
        Assert.Equal("${secret:store:x:enable}", result.GetProperty("enable_secret").GetString());
        Assert.False(result.TryGetProperty("structured", out _));
        Assert.False(result.TryGetProperty("enable", out _));
    }

    [Fact]
    public async Task Ssh_device_names_an_inventory_device_and_an_unknown_one_is_not_found()
    {
        using var db = TestDb.NewContext();
        var h = Ssh(db);

        var result = await h.ExecuteAsync(
            Req("ssh", new { commands = new[] { "show version" }, device = "core-sw-1" }),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not_found", result.Error);
        Assert.Contains("core-sw-1", result.Error);
    }

    [Fact]
    public async Task Ssh_device_resolves_the_host_from_the_inventory()
    {
        using var db = TestDb.NewContext(nameof(Ssh_device_resolves_the_host_from_the_inventory));
        SeedDevice(db, "core-sw-1", "10.10.0.1");
        var h = Ssh(db);

        var result = await h.ExecuteAsync(
            Req("ssh", new { commands = new[] { "show version" }, device = "core-sw-1" }),
            CancellationToken.None);

        // The device has no credential, so the step still fails — but it fails
        // on the CREDENTIAL, which is only reachable once the host resolved.
        Assert.False(result.Success);
        Assert.Contains("credential", result.Error);
        Assert.DoesNotContain("cannot resolve SSH host", result.Error);
    }

    // ── ping ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ping_device_names_an_inventory_device()
    {
        using var db = TestDb.NewContext(nameof(Ping_device_names_an_inventory_device));
        SeedDevice(db, "edge-1", "an address that is not one");
        var h = new PingHandler(new DeviceRepository(db), NullLogger<PingHandler>.Instance);

        var result = await h.ExecuteAsync(Req("ping", new { device = "edge-1" }), CancellationToken.None);

        // Proof the NAME was resolved to that device's stored address: the
        // failure names the address, not the device.
        Assert.False(result.Success);
        Assert.Contains("an address that is not one", result.Error);
    }

    [Fact]
    public async Task Ping_an_unknown_device_name_is_not_found()
    {
        using var db = TestDb.NewContext(nameof(Ping_an_unknown_device_name_is_not_found));
        var h = new PingHandler(new DeviceRepository(db), NullLogger<PingHandler>.Instance);

        var result = await h.ExecuteAsync(Req("ping", new { device = "nowhere" }), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not_found", result.Error);
    }

    // ── report ────────────────────────────────────────────────────────────

    [Fact]
    public void Report_markdown_content_becomes_the_equivalent_document()
    {
        var result = Normalize("report", """{"format":"pdf","title":"Audit","content":"# Findings\n- none"}""");

        var document = result.GetProperty("document");
        Assert.Equal("Audit", document.GetProperty("title").GetString());
        var section = Assert.Single(document.GetProperty("sections").EnumerateArray().ToList());
        Assert.Equal("# Findings\n- none", section.GetProperty("description").GetString());
        Assert.False(result.TryGetProperty("content", out _));
        Assert.Equal("pdf", result.GetProperty("format").GetString());
    }

    [Fact]
    public void Report_a_declared_document_wins_over_content()
    {
        var result = Normalize(
            "report", """{"format":"html","document":{"title":"Real","sections":[]},"content":"ignored"}""");

        Assert.Equal("Real", result.GetProperty("document").GetProperty("title").GetString());
    }

    // ── slack_message ─────────────────────────────────────────────────────

    // `via` names a messaging-channel record to post through. This product posts with the
    // deployment's bot token, so the key is inert here — and it USED to be deleted for that
    // reason. It is not any more: inert is not the same as absent. snippets/SPEC.md says an
    // unrecognised key survives normalisation untouched, and a key silently deleted is
    // indistinguishable from one the author never wrote. It now reaches the handler, which
    // ignores it, and the pipeline's unknown-key note says so out loud.
    [Fact]
    public void Slack_via_survives_because_inert_is_not_absent()
    {
        var result = Normalize("slack_message", """{"channel":"#alerts","text":"hi","via":"ops-workspace"}""");

        Assert.Equal("ops-workspace", result.GetProperty("via").GetString());
        Assert.Equal("#alerts", result.GetProperty("channel").GetString());
        Assert.Equal("hi", result.GetProperty("text").GetString());
    }

    // ── a redundant alias loses to its canonical key ──────────────────────

    // Both handlers already resolve their pair this way. Declaring it in the normaliser does
    // not change what they do — it moves the decision out of fourteen handlers, each of which
    // is otherwise a chance to decide it differently.

    [Fact]
    public void Ping_device_loses_to_host_and_is_dropped()
    {
        var result = Normalize("ping", """{"host":"10.0.0.1","device":"core-1"}""");

        Assert.Equal("10.0.0.1", result.GetProperty("host").GetString());
        Assert.False(result.TryGetProperty("device", out _));
    }

    // The half that makes `device` a special case rather than an alias: on its own it is an
    // INVENTORY NAME the handler resolves against the device table. Renaming it to `host`
    // would hand an inventory name straight to the prober, where a permissive resolver
    // answers with whatever it likes.
    [Fact]
    public void Ping_device_alone_is_left_exactly_as_written()
    {
        var result = Normalize("ping", """{"device":"core-1"}""");

        Assert.Equal("core-1", result.GetProperty("device").GetString());
        Assert.False(result.TryGetProperty("host", out _));
    }

    [Fact]
    public void Ssh_command_loses_to_commands_and_is_dropped()
    {
        // The two disagree on purpose. Handing the handler both makes the answer depend on
        // which key it reads first, and this is a `non_reversible` type — the wrong answer
        // reloads a device.
        var result = Normalize("ssh", """{"commands":["show run"],"command":"reload"}""");

        Assert.Equal("show run", result.GetProperty("commands")[0].GetString());
        Assert.False(result.TryGetProperty("command", out _));
    }

    [Fact]
    public void Ssh_command_alone_is_left_exactly_as_written()
    {
        var result = Normalize("ssh", """{"command":"show version"}""");

        Assert.Equal("show version", result.GetProperty("command").GetString());
        Assert.False(result.TryGetProperty("commands", out _));
    }

    // The guard the handlers keep for themselves. A handler can be reached with a payload
    // that never passed through the normaliser, and the cost of getting this wrong is running
    // a command the author did not write.
    [Fact]
    public void The_ssh_handler_still_resolves_the_pair_itself()
    {
        var raw = TestJson.Element("""{"commands":["show run"],"command":"reload"}""");

        var resolved = SshHandler.ResolveCommandsForTest(raw, out var error);

        Assert.Null(error);
        Assert.Equal(["show run"], resolved);
    }

    // ── ansible_playbook ──────────────────────────────────────────────────

    [Fact]
    public void Ansible_device_and_targets_become_hosts()
    {
        Assert.Equal(
            "core-sw-1",
            Normalize("ansible_playbook", """{"device":"core-sw-1"}""").GetProperty("hosts").GetString());

        var fromTargets = Normalize("ansible_playbook", """{"targets":["a","b"]}""").GetProperty("hosts");
        Assert.Equal(JsonValueKind.Array, fromTargets.ValueKind);
        Assert.Equal(2, fromTargets.GetArrayLength());
        // Not just the LENGTH: the handler used to read the array and run the
        // playbook against .First(), so an alias test that stopped at the count
        // passed while two of three hosts were never touched.
        Assert.Equal(["a", "b"], fromTargets.EnumerateArray().Select(e => e.GetString()).ToList());

        // `host` carries a literal address and is an alias of `hosts` too
        // (snippets/SPEC.md `ansible_playbook`). Without it, {"host":"10.0.0.1"}
        // failed with "cannot resolve ansible target" while SnippetKeyCatalog
        // claimed the key was known, so the import noted nothing either.
        Assert.Equal(
            "10.0.0.1",
            Normalize("ansible_playbook", """{"host":"10.0.0.1"}""").GetProperty("hosts").GetString());
    }

    [Fact]
    public void Ansible_every_host_reaches_the_inventory_not_only_the_first()
    {
        // The inventory the handler writes is what proves it: one [targets]
        // line per host. `AnsibleHandler.ResolveTargetsAsync` is private, so
        // this pins the contract the handler depends on — the normalized
        // payload keeps the whole list.
        var hosts = Normalize("ansible_playbook", """{"targets":["a","b","c"]}""").GetProperty("hosts");

        Assert.Equal(3, hosts.GetArrayLength());
        Assert.Equal(["a", "b", "c"], hosts.EnumerateArray().Select(e => e.GetString()).ToList());
    }

    [Fact]
    public void Ansible_a_declared_hosts_wins_and_the_aliases_are_dropped()
    {
        var result = Normalize(
            "ansible_playbook",
            """{"hosts":"all","device":"core-sw-1","targets":["x"],"host":"10.0.0.1"}""");

        Assert.Equal("all", result.GetProperty("hosts").GetString());
        Assert.False(result.TryGetProperty("device", out _));
        Assert.False(result.TryGetProperty("targets", out _));
        Assert.False(result.TryGetProperty("host", out _));
    }

    // ── rest_call: two forms, one type ────────────────────────────────────

    [Fact]
    public void The_catalogued_rest_call_form_is_recognised_and_the_raw_one_is_not()
    {
        Assert.Equal(
            ["source", "operation_id", "path_params"],
            PayloadAliases.CataloguedRestCall(
                BundleTest.Json("""{"source":"netbox","operation_id":"dcim_devices_list","path_params":{"id":"1"}}""")));

        // A node with a url is the raw form even if it carries a stray key.
        Assert.Empty(PayloadAliases.CataloguedRestCall(
            BundleTest.Json("""{"url":"https://x.test/api","source":"netbox"}""")));
        Assert.Empty(PayloadAliases.CataloguedRestCall(BundleTest.Json("""{"url":"https://x.test/api"}""")));
    }

    [Fact]
    public async Task A_catalogued_rest_call_fails_with_not_supported_naming_the_keys()
    {
        var h = new RestCallHandler(
            new FakeHttpClientFactory(new FakeHttpMessageHandler(System.Net.HttpStatusCode.OK, "{}")),
            new flow_weaver_backend.Services.Net.UrlGuard(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                NullLogger<flow_weaver_backend.Services.Net.UrlGuard>.Instance),
            new PassThroughSecrets(),
            NullLogger<RestCallHandler>.Instance);

        var result = await h.ExecuteAsync(
            Req("rest_call", new { source = "netbox", operation_id = "dcim_devices_list" }),
            CancellationToken.None);

        Assert.False(result.Success);
        // The author needs to know the FORM is unsupported, not that a key is
        // absent — "input.url is required" sends them looking for a typo.
        Assert.Contains("not_supported", result.Error);
        Assert.Contains("source", result.Error);
        Assert.Contains("operation_id", result.Error);
        Assert.Contains("integration_action", result.Error);
    }

    // ── git ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Git_repository_names_a_registered_repository()
    {
        using var db = TestDb.NewContext(nameof(Git_repository_names_a_registered_repository));
        var repoId = Guid.NewGuid();
        db.GitRepositories.Add(new GitRepository
        {
            GitRepositoryId = repoId, Name = "configs", Url = "https://git.test/configs.git",
            DefaultBranch = "main", IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        var git = new RecordingGitService();
        var h = new GitOpsHandler(
            git, new GitRepositoryRepository(db), new MutableCurrentUser(new HttpContextAccessor()),
            NullLogger<GitOpsHandler>.Instance);

        var result = await h.ExecuteAsync(
            Req("git", new { operation = "pull", repository = "configs", branch = "main" }),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(repoId, git.LastId);
    }

    [Fact]
    public async Task Git_an_unknown_repository_name_is_not_found()
    {
        using var db = TestDb.NewContext(nameof(Git_an_unknown_repository_name_is_not_found));
        var h = new GitOpsHandler(
            new RecordingGitService(), new GitRepositoryRepository(db),
            new MutableCurrentUser(new HttpContextAccessor()), NullLogger<GitOpsHandler>.Instance);

        var result = await h.ExecuteAsync(
            Req("git", new { operation = "pull", repository = "configs" }), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not_found", result.Error);
        Assert.Contains("configs", result.Error);
    }

    // ── the rule that keeps this safe ─────────────────────────────────────

    [Fact]
    public void A_payload_with_nothing_to_rewrite_comes_back_untouched()
    {
        var original = BundleTest.Json("""{"url":"https://x.test","method":"GET","headers":{"A":"b"}}""");

        var result = PayloadAliases.Normalize("rest_call", original);

        Assert.Equal(original.GetRawText(), result.GetRawText());
    }

    [Fact]
    public void An_unknown_type_and_a_non_object_payload_are_passed_through()
    {
        Assert.Equal(
            JsonValueKind.Array,
            PayloadAliases.Normalize("ssh", BundleTest.Json("[1,2]")).ValueKind);
        var untouched = BundleTest.Json("""{"mapping":{"a":"b"}}""");
        Assert.True(PayloadAliases.Normalize("python_snippet", untouched).TryGetProperty("mapping", out _));
    }

    [Fact]
    public void Keys_no_alias_covers_are_left_alone_for_the_handler_to_ignore()
    {
        // snippets/SPEC.md rule 3: extra keys are allowed; the importer notes
        // them per node. Nothing here may quietly delete one.
        var result = Normalize("ssh", """{"commands":["a"],"structured":true,"vendor_quirk":"nokia"}""");

        Assert.Equal("nokia", result.GetProperty("vendor_quirk").GetString());
    }

    // ── fixtures ──────────────────────────────────────────────────────────

    private static SshHandler Ssh(AppDbContext db, flow_weaver_backend.Services.Ai.Secrets.ISecretResolver? secrets = null)
        => new(new DeviceRepository(db), new RepositoryBase<CredentialModel>(db),
               new WorkflowRunRepository(db), new FakeCrypto(), new FakePolicyEvaluator(),
               new FakeVendorCommandValidator(), secrets ?? new PassThroughSecrets(),
               new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
               NullLogger<SshHandler>.Instance);

    private static void SeedDevice(AppDbContext db, string name, string ip)
    {
        db.Devices.Add(new Device
        {
            DeviceId = Guid.NewGuid(),
            DeviceName = name,
            IpAddress = ip,
            Platform = "cisco_ios",
            CredentialId = Guid.Empty,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }
}

file sealed class RecordingMcpExecutor : IMcpToolExecutor
{
    public Guid LastServer;
    public string? LastTool;

    public Task<McpCallResult> ExecuteAsync(
        Guid mcpServerId, string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        LastServer = mcpServerId;
        LastTool = toolName;
        return Task.FromResult(new McpCallResult("ok", null, false));
    }
}

// Only the one operation these tests drive is implemented; anything else is a
// test that wandered off its path and should say so loudly.
file sealed class RecordingGitService : IGitService
{
    public Guid LastId;

    public Task<ActionResult<GitOpResult>> PullAsync(Guid id, string? branch, CancellationToken ct)
    {
        LastId = id;
        return Task.FromResult<ActionResult<GitOpResult>>(
            new GitOpResult { Ok = true, Message = "pulled", Branch = branch });
    }

    public Task<ActionResult<ListResponse<GitRepositoryResponse>>> ListAsync(int limit, int offset, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActionResult<GitRepositoryResponse>> GetAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActionResult<GitRepositoryResponse>> CreateAsync(CreateGitRepository dto, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActionResult<GitRepositoryResponse>> UpdateAsync(Guid id, UpdateGitRepository dto, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActionResult<GitRepositoryResponse>> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActionResult<GitOpResult>> PushAsync(Guid id, string? branch, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActionResult<GitBranchesResponse>> ListBranchesAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActionResult<GitOpResult>> CheckoutAsync(Guid id, string branch, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActionResult<GitListFilesResponse>> ListFilesAsync(Guid id, string? path, string? @ref, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActionResult<GitReadFileResponse>> ReadFileAsync(Guid id, string path, string? @ref, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActionResult<GitOpResult>> WriteFileAsync(Guid id, GitWriteFileRequest req, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActionResult<GitOpResult>> CommitAsync(Guid id, GitCommitRequest req, CancellationToken ct) => throw new NotSupportedException();
    public Task<ActionResult<GitDiffResponse>> DiffAsync(Guid id, string? @from, string? to, string? path, CancellationToken ct) => throw new NotSupportedException();
}
