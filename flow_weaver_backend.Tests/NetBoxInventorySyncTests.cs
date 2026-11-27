using System.Net;
using System.Text;
using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.InventorySource;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using InventorySourceModel = flow_weaver_backend.Models.InventorySource;

namespace flow_weaver_backend.Tests;

// InventorySourceService wired to an unreachable NetBox, for CRUD tests that
// never sync.
internal static class TestInventory
{
    public static readonly JsonElement NetBoxConfig =
        JsonSerializer.SerializeToElement(new { base_url = "https://netbox.example" });

    public static InventorySourceService Service(AppDbContext db, FakeUser user) => new(
        new RepositoryBase<InventorySourceModel>(db), user, new FakeAudit(),
        NullLogger<InventorySourceService>.Instance,
        new NetBoxSyncService(db, new PassThroughSecrets(), new PassUrlGuard(),
            new FakeHttpClientFactory(new FakeHttpMessageHandler(HttpStatusCode.ServiceUnavailable)),
            NullLogger<NetBoxSyncService>.Instance),
        new SecretRepository(db), new IntegrationRepository(db), new CredentialRepository(db));
}

// NetBox device JSON -> Device mapping (ported from Nashira's NetBoxMappingTests).
public class NetBoxMappingTests
{
    [Fact]
    public void MapDevice_maps_core_fields_and_strips_ip_prefix()
    {
        const string json = """
            {"name":"edge-01",
             "primary_ip4":{"address":"10.1.2.3/24"},
             "platform":{"slug":"cisco_ios","name":"Cisco IOS"},
             "device_type":{"manufacturer":{"name":"Cisco"}},
             "site":{"name":"HQ","slug":"hq"},
             "role":{"name":"edge"},
             "status":{"value":"active","label":"Active"}}
            """;
        using var doc = JsonDocument.Parse(json);

        var m = NetBoxSyncService.MapDevice(doc.RootElement);

        Assert.NotNull(m);
        Assert.Equal("edge-01", m!.Name);
        Assert.Equal("10.1.2.3", m.Ip);
        Assert.Equal("cisco_ios", m.Platform);
        Assert.Equal("Cisco", m.Vendor);
        Assert.Equal("HQ", m.Site);
        Assert.Equal("edge", m.Role);
        Assert.Equal("active", m.Status);
    }

    [Fact]
    public void MapDevice_falls_back_to_device_role_and_string_status()
    {
        using var doc = JsonDocument.Parse("""{"name":"sw1","device_role":{"name":"access"},"status":"offline"}""");

        var m = NetBoxSyncService.MapDevice(doc.RootElement);

        Assert.Equal("access", m!.Role);
        Assert.Equal("offline", m.Status);
        Assert.Equal(string.Empty, m.Ip);
        Assert.Equal(string.Empty, m.Platform);
    }

    [Fact]
    public void MapDevice_returns_null_when_name_is_missing()
    {
        using var doc = JsonDocument.Parse("""{"status":"active"}""");
        Assert.Null(NetBoxSyncService.MapDevice(doc.RootElement));
    }

    [Fact]
    public void MapDevice_stringifies_the_numeric_netbox_id()
    {
        using var doc = JsonDocument.Parse("""{"name":"edge-01","id":4271}""");
        Assert.Equal("4271", NetBoxSyncService.MapDevice(doc.RootElement)!.ExternalId);
    }

    [Fact]
    public void MapDevice_carries_custom_fields_tags_and_serial_into_properties()
    {
        const string json = """
            {"name":"edge-01",
             "serial":"FTX1234",
             "custom_fields":{"maintenance_window":"sun-0200"},
             "tags":[{"name":"core"},{"slug":"managed"}],
             "tenant":{"name":"acme"},
             "device_type":{"model":"ISR4331"}}
            """;
        using var doc = JsonDocument.Parse(json);

        var props = NetBoxSyncService.MapDevice(doc.RootElement)!.Properties;

        Assert.Equal("FTX1234", props.GetProperty("serial").GetString());
        Assert.Equal("acme", props.GetProperty("tenant").GetString());
        Assert.Equal("ISR4331", props.GetProperty("device_type").GetString());
        Assert.Equal("sun-0200", props.GetProperty("custom_fields").GetProperty("maintenance_window").GetString());
        Assert.Equal(["core", "managed"], props.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList());
    }

    [Fact]
    public void MapDevice_produces_an_empty_object_when_there_is_nothing_extra()
    {
        using var doc = JsonDocument.Parse("""{"name":"edge-01"}""");
        var props = NetBoxSyncService.MapDevice(doc.RootElement)!.Properties;
        Assert.Equal(JsonValueKind.Object, props.ValueKind);
        Assert.Empty(props.EnumerateObject());
    }

    [Theory]
    [InlineData("nbt_abc.def", "Bearer nbt_abc.def")]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "Token 0123456789abcdef0123456789abcdef01234567")]
    [InlineData("Bearer xyz", "Bearer xyz")]
    public void AuthorizationHeader_picks_the_scheme_from_the_token_version(string token, string expected)
        => Assert.Equal(expected, NetBoxSyncService.AuthorizationHeader(token));
}

// End-to-end sync against a scripted NetBox, through InventorySourceService.
public class NetBoxInventorySyncTests
{
    private const string TokenRef = "${secret:secret:netbox:value}";

    private static string Page(string? next, params string[] devices) =>
        $$"""{"count":{{devices.Length}},"next":{{(next is null ? "null" : $"\"{next}\"")}},"results":[{{string.Join(",", devices)}}]}""";

    private static string Dev(int id, string name, string ip = "10.0.0.1/24", string status = "active") =>
        JsonSerializer.Serialize(new
        {
            id, name,
            primary_ip4 = new { address = ip },
            platform = new { slug = "arista_eos" },
            device_type = new { manufacturer = new { name = "Arista" }, model = "cEOS" },
            site = new { name = "lab" },
            role = new { name = "leaf" },
            status = new { value = status },
        });

    private sealed class Fixture : IDisposable
    {
        public AppDbContext Db { get; } = TestDb.NewContext();
        public FakeHttpMessageHandler Http { get; }
        public PassThroughSecrets Secrets { get; } = new((TokenRef, "nbt_key.secret"));
        public FakeUser User { get; init; } = new();

        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> responder) => Http = new FakeHttpMessageHandler(responder);

        public InventorySourceService Svc(FakeUser? user = null) => new(
            new RepositoryBase<InventorySourceModel>(Db), user ?? User, new FakeAudit(),
            NullLogger<InventorySourceService>.Instance,
            new NetBoxSyncService(Db, Secrets, new PassUrlGuard(), new FakeHttpClientFactory(Http),
                NullLogger<NetBoxSyncService>.Instance),
            new SecretRepository(Db), new IntegrationRepository(Db), new CredentialRepository(Db));

        public void AddSecret(string name)
        {
            Db.Secrets.Add(new Secret
            {
                SecretId = Guid.NewGuid(), Name = name, EncryptedValue = Encoding.UTF8.GetBytes("x"),
                IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            Db.SaveChanges();
        }

        public InventorySourceModel AddSource(string? tokenRef = TokenRef, string baseUrl = "https://netbox.example")
        {
            var cfg = new Dictionary<string, object?> { ["base_url"] = baseUrl, ["allow_private_network"] = true };
            if (tokenRef is not null) cfg["token_secret_ref"] = tokenRef;
            var s = new InventorySourceModel
            {
                InventorySourceId = Guid.NewGuid(), Name = "netbox", Type = "netbox", Enabled = true,
                Config = JsonSerializer.SerializeToElement(cfg), Mapping = JsonSerializer.SerializeToElement(new { }),
                SyncMode = "manual", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            Db.InventorySources.Add(s);
            Db.SaveChanges();
            return s;
        }

        public void Dispose() => Db.Dispose();
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Sync_creates_devices_across_pages_with_bearer_v2_token()
    {
        using var f = new Fixture(req => req.RequestUri!.Query.Contains("offset=2")
            ? Json(Page(null, Dev(3, "leaf3")))
            : Json(Page("https://netbox.example/api/dcim/devices/?limit=200&offset=2", Dev(1, "leaf1"), Dev(2, "leaf2"))));
        var src = f.AddSource();

        var r = (await f.Svc().SyncAsync(src.InventorySourceId, dryRun: false)).Value!;

        Assert.Equal((3, 0, 0, 3), (r.Created, r.Updated, r.Skipped, r.Total));
        Assert.Equal(3, f.Db.Devices.Count());
        var leaf1 = f.Db.Devices.Single(d => d.DeviceName == "leaf1");
        Assert.Equal(("10.0.0.1", "arista_eos", "Arista", "lab", "leaf", "active", "1"),
            (leaf1.IpAddress, leaf1.Platform, leaf1.Vendor, leaf1.Site, leaf1.Role, leaf1.Status, leaf1.ExternalId));
        Assert.Equal(src.InventorySourceId, leaf1.SourceId);
        Assert.Equal("Bearer nbt_key.secret", f.Http.Requests[0].Headers.Authorization!.ToString());
        Assert.StartsWith("ok: 3 created", f.Db.InventorySources.Single().LastSyncStatus);
        Assert.NotNull(f.Db.InventorySources.Single().LastSyncAt);
    }

    [Fact]
    public async Task Resync_is_idempotent_and_follows_a_rename_by_external_id()
    {
        var name = "leaf1";
        using var f = new Fixture(_ => Json(Page(null, Dev(1, name))));
        var src = f.AddSource();
        await f.Svc().SyncAsync(src.InventorySourceId, false);

        var again = (await f.Svc().SyncAsync(src.InventorySourceId, false)).Value!;
        Assert.Equal((0, 0, 1), (again.Created, again.Updated, again.Unchanged));

        name = "leaf1-renamed";
        var renamed = (await f.Svc().SyncAsync(src.InventorySourceId, false)).Value!;
        Assert.Equal((0, 1), (renamed.Created, renamed.Updated));
        Assert.Equal("leaf1-renamed", f.Db.Devices.Single().DeviceName);
    }

    [Fact]
    public async Task Sync_adopts_a_manual_device_by_name_and_keeps_its_credential()
    {
        using var f = new Fixture(_ => Json(Page(null, Dev(7, "core1", ip: "10.9.9.9/32"))));
        var cred = Guid.NewGuid();
        f.Db.Devices.Add(new Device
        {
            DeviceId = Guid.NewGuid(), DeviceName = "core1", IpAddress = "1.1.1.1", CredentialId = cred,
            Properties = JsonSerializer.SerializeToElement(new { }), IsActive = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        f.Db.SaveChanges();
        var src = f.AddSource();

        var r = (await f.Svc().SyncAsync(src.InventorySourceId, false)).Value!;

        Assert.Equal((0, 1), (r.Created, r.Updated));
        var d = f.Db.Devices.Single();
        Assert.Equal(("10.9.9.9", "7", cred), (d.IpAddress, d.ExternalId, d.CredentialId));
        Assert.Equal(src.InventorySourceId, d.SourceId);
    }

    [Fact]
    public async Task Two_netbox_devices_sharing_a_name_do_not_collapse_into_one_row()
    {
        using var f = new Fixture(_ => Json(Page(null, Dev(1, "dup"), Dev(2, "dup"))));
        var src = f.AddSource();

        var r = (await f.Svc().SyncAsync(src.InventorySourceId, false)).Value!;

        Assert.Equal((1, 1, 2), (r.Created, r.Skipped, r.Total));
        Assert.Single(f.Db.Devices);
    }

    [Fact]
    public async Task Dry_run_reports_without_writing()
    {
        using var f = new Fixture(_ => Json(Page(null, Dev(1, "leaf1"), Dev(2, "leaf2"))));
        var src = f.AddSource();

        var r = (await f.Svc().SyncAsync(src.InventorySourceId, dryRun: true)).Value!;

        Assert.True(r.DryRun);
        Assert.Equal(2, r.Created);
        Assert.Empty(f.Db.Devices);
        Assert.Null(f.Db.InventorySources.Single().LastSyncAt);
    }

    [Fact]
    public async Task Netbox_403_is_a_502_with_a_hint_and_is_recorded_on_the_source()
    {
        using var f = new Fixture(_ => Json("""{"detail":"Invalid v2 token"}""", HttpStatusCode.Forbidden));
        var src = f.AddSource();

        var res = await f.Svc().SyncAsync(src.InventorySourceId, false);

        var obj = Assert.IsType<ObjectResult>(res.Result);
        Assert.Equal(502, obj.StatusCode);
        Assert.Contains("NetBox rejected the token", JsonSerializer.Serialize(obj.Value));
        Assert.StartsWith("failed: netbox request failed (403)", f.Db.InventorySources.Single().LastSyncStatus);
        Assert.Empty(f.Db.Devices);
    }

    [Fact]
    public async Task Unresolved_token_reference_fails_before_calling_netbox()
    {
        using var f = new Fixture(_ => Json(Page(null)));
        var src = f.AddSource(tokenRef: "${secret:secret:gone:value}");

        var res = await f.Svc().SyncAsync(src.InventorySourceId, false);

        Assert.Equal(400, Assert.IsType<ObjectResult>(res.Result).StatusCode);
        Assert.Empty(f.Http.Requests);
    }

    [Fact]
    public async Task Non_netbox_source_cannot_be_synced()
    {
        using var f = new Fixture(_ => Json(Page(null)));
        var src = f.AddSource();
        src.Type = "csv";
        f.Db.SaveChanges();

        var res = await f.Svc().SyncAsync(src.InventorySourceId, false);

        Assert.Equal(400, Assert.IsType<ObjectResult>(res.Result).StatusCode);
    }

    // ─── config validation on create/update ─────────────────────────

    private static CreateInventorySource Create(object config) => new()
    {
        Name = "nb", Type = "netbox", Config = JsonSerializer.SerializeToElement(config),
    };

    [Fact]
    public async Task Create_rejects_a_raw_token()
    {
        using var f = new Fixture(_ => Json(Page(null)));

        var res = await f.Svc().PostAsync(Create(new { base_url = "https://nb", token_secret_ref = "nbt_abc.0123456789" }));

        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Empty(f.Db.InventorySources);
    }

    [Fact]
    public async Task Create_rejects_a_missing_base_url()
    {
        using var f = new Fixture(_ => Json(Page(null)));
        Assert.IsType<BadRequestObjectResult>((await f.Svc().PostAsync(Create(new { site_filter = "hq" }))).Result);
    }

    [Fact]
    public async Task Create_normalizes_a_bare_secret_name_into_a_reference()
    {
        using var f = new Fixture(_ => Json(Page(null)));
        f.AddSecret("netbox");

        var res = await f.Svc().PostAsync(Create(new { base_url = "https://nb", token_secret_ref = "netbox" }));

        var body = (InventorySourceResponse)Assert.IsType<CreatedAtActionResult>(res.Result).Value!;
        Assert.Equal(TokenRef, body.Config.GetProperty("token_secret_ref").GetString());
    }

    [Fact]
    public async Task Create_rejects_a_reference_to_a_secret_that_does_not_exist()
    {
        using var f = new Fixture(_ => Json(Page(null)));
        var res = await f.Svc().PostAsync(Create(new { base_url = "https://nb", token_secret_ref = "${secret:secret:nope:value}" }));
        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task Non_admin_cannot_attach_a_token_reference()
    {
        using var f = new Fixture(_ => Json(Page(null)));
        f.AddSecret("netbox");
        var operatorUser = new FakeUser { Roles = new[] { "operator" } };

        var res = await f.Svc(operatorUser).PostAsync(Create(new { base_url = "https://nb", token_secret_ref = TokenRef }));

        Assert.Equal(403, Assert.IsType<ObjectResult>(res.Result).StatusCode);
    }

    [Fact]
    public async Task Non_admin_cannot_move_base_url_of_an_authenticated_source_but_can_edit_the_rest()
    {
        using var f = new Fixture(_ => Json(Page(null)));
        f.AddSecret("netbox");
        var src = f.AddSource();
        var operatorUser = new FakeUser { Roles = new[] { "operator" } };

        var moved = await f.Svc(operatorUser).UpdateAsync(src.InventorySourceId, new UpdateInventorySource
        {
            Config = JsonSerializer.SerializeToElement(new { base_url = "https://evil.example", token_secret_ref = TokenRef }),
        });
        Assert.Equal(403, Assert.IsType<ObjectResult>(moved.Result).StatusCode);

        var filtered = await f.Svc(operatorUser).UpdateAsync(src.InventorySourceId, new UpdateInventorySource
        {
            Config = JsonSerializer.SerializeToElement(new { base_url = "https://netbox.example", token_secret_ref = TokenRef, site_filter = "hq" }),
        });
        Assert.Equal("hq", filtered.Value!.Config.GetProperty("site_filter").GetString());
    }

    [Fact]
    public async Task A_stored_literal_token_is_masked_and_an_echoed_mask_keeps_it()
    {
        using var f = new Fixture(_ => Json(Page(null)));
        var src = f.AddSource(tokenRef: "legacy-raw-token");

        var got = (await f.Svc().GetByIdAsync(src.InventorySourceId)).Value!;
        Assert.Equal("***", got.Config.GetProperty("token_secret_ref").GetString());

        await f.Svc().UpdateAsync(src.InventorySourceId, new UpdateInventorySource
        {
            Config = JsonSerializer.SerializeToElement(new { base_url = "https://netbox.example", token_secret_ref = "***" }),
        });
        Assert.Equal("legacy-raw-token",
            f.Db.InventorySources.Single().Config.GetProperty("token_secret_ref").GetString());
    }
}
