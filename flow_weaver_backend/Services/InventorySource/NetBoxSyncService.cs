using System.Text.Json;
using flow_weaver_backend.Data.Db;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Secrets;
using flow_weaver_backend.Services.Net;
using Microsoft.EntityFrameworkCore;
using DeviceModel = flow_weaver_backend.Models.Device;
using InventorySourceModel = flow_weaver_backend.Models.InventorySource;

namespace flow_weaver_backend.Services.InventorySource;

// Port of Nashira's NetBox inventory sync. Reads /api/dcim/devices/ page by page
// and upserts each device. Changes are staged on the request's AppDbContext and
// saved once at the end, so a sync that fails half-way writes nothing.
public sealed class NetBoxSyncService : INetBoxSyncService
{
    private const int PageSize = 200;
    private const int MaxPages = 200; // hard stop against a runaway paginator

    private readonly AppDbContext _db;
    private readonly ISecretResolver _secrets;
    private readonly IUrlGuard _urlGuard;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NetBoxSyncService> _logger;

    public NetBoxSyncService(
        AppDbContext db, ISecretResolver secrets, IUrlGuard urlGuard,
        IHttpClientFactory httpFactory, ILogger<NetBoxSyncService> logger)
    {
        _db = db;
        _secrets = secrets;
        _urlGuard = urlGuard;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<InventorySyncResult> SyncAsync(InventorySourceModel source, bool dryRun, CancellationToken ct)
    {
        if (!NetBoxSourceConfig.IsNetBox(source.Type))
            throw new InventorySyncException($"inventory source type '{source.Type}' cannot be synced; only 'netbox' is supported");

        var cfg = NetBoxSourceConfig.Parse(source.Config);
        if (NetBoxSourceConfig.ValidateBaseUrl(cfg.BaseUrl) is { } urlError)
            throw new InventorySyncException(urlError);
        var baseUrl = cfg.BaseUrl.TrimEnd('/');

        var token = await ResolveTokenAsync(cfg.TokenSecretRef, ct);

        var url = $"{baseUrl}/api/dcim/devices/?limit={PageSize}";
        if (cfg.SiteFilter is { } site)
            url += $"&site={Uri.EscapeDataString(site)}";

        var client = _httpFactory.CreateClient("rest_call");

        // Two lookups, in priority order. `byExternal` holds the rows this source
        // already created, keyed by NetBox's own id — matching on it means a device
        // renamed in NetBox updates its row instead of forking a second one. The
        // name map is the fallback that adopts pre-existing rows (created by hand or
        // imported) into this source on the first pass; an active row wins over a
        // soft-deleted one of the same name.
        var all = await _db.Devices.ToListAsync(ct);
        var byExternal = all
            .Where(d => d.SourceId == source.InventorySourceId && d.ExternalId != null)
            .GroupBy(d => d.ExternalId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.IsActive).First(), StringComparer.Ordinal);
        var byName = all
            .GroupBy(d => d.DeviceName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.IsActive).First(), StringComparer.Ordinal);

        var result = new InventorySyncResult { DryRun = dryRun };
        var now = DateTime.UtcNow;
        var pages = 0;

        while (!string.IsNullOrEmpty(url) && pages < MaxPages)
        {
            pages++;
            try
            {
                _urlGuard.EnsureSafe(url, allowPrivate: cfg.AllowPrivateNetwork);
            }
            catch (InvalidOperationException ex)
            {
                throw new InventorySyncException(
                    $"inventory source URL rejected: {ex.Message}"
                    + (cfg.AllowPrivateNetwork ? string.Empty : " (set config.allow_private_network for an on-prem NetBox)"));
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (!string.IsNullOrEmpty(token))
                req.Headers.TryAddWithoutValidation("Authorization", AuthorizationHeader(token));

            HttpResponseMessage resp;
            try
            {
                resp = await client.SendAsync(req, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw new InventorySyncException($"could not reach NetBox at {baseUrl}: {ex.Message}", 502);
            }

            using var respLease = resp;
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var code = (int)resp.StatusCode;
                var hint = code is 401 or 403
                    ? (token is null
                        ? " — the source has no token; set config.token_secret_ref"
                        : " — NetBox rejected the token")
                    : string.Empty;
                throw new InventorySyncException($"netbox request failed ({code}){hint}: {Truncate(body, 200)}", 502);
            }

            JsonDocument doc;
            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                doc = await JsonDocument.ParseAsync(stream, default, ct);
            }
            catch (JsonException)
            {
                throw new InventorySyncException(
                    $"NetBox at {baseUrl} did not answer with JSON; check that base_url points at NetBox", 502);
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in results.EnumerateArray())
                        Upsert(el, source.InventorySourceId, dryRun, now, byExternal, byName, result);
                }

                url = root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String
                    ? next.GetString()
                    : null;
            }
        }

        if (!dryRun)
            await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "inventory_source.sync source={Source} dry_run={DryRun} created={Created} updated={Updated} unchanged={Unchanged} skipped={Skipped} total={Total} pages={Pages}",
            source.Name, dryRun, result.Created, result.Updated, result.Unchanged, result.Skipped, result.Total, pages);
        return result;
    }

    private void Upsert(
        JsonElement el, Guid sourceId, bool dryRun, DateTime now,
        Dictionary<string, DeviceModel> byExternal, Dictionary<string, DeviceModel> byName,
        InventorySyncResult result)
    {
        result.Total++;
        var mapped = MapDevice(el);
        if (mapped is null)
        {
            // A record with no usable name cannot become a row. Count it — silently
            // dropping it makes "NetBox has more devices than the inventory" undiagnosable.
            _logger.LogWarning("inventory_source.sync.unnamed_device external_id={ExternalId}", IdOf(el));
            result.Skipped++;
            return;
        }

        var dev = mapped.ExternalId is { } ext && byExternal.TryGetValue(ext, out var byExt)
            ? byExt
            : byName.GetValueOrDefault(mapped.Name);

        // The name fallback exists to adopt rows without provenance. A row this
        // source already tracks under a *different* NetBox id is a different device
        // that shares the name — NetBox does not enforce unique names — and merging
        // would silently collapse the two into one row.
        if (dev is not null && mapped.ExternalId is not null
            && dev.SourceId == sourceId
            && dev.ExternalId is not null && dev.ExternalId != mapped.ExternalId)
        {
            _logger.LogWarning(
                "inventory_source.sync.duplicate_name name={Name} kept_external_id={Kept} skipped_external_id={Skipped}",
                mapped.Name, dev.ExternalId, mapped.ExternalId);
            result.Skipped++;
            return;
        }

        if (dev is not null)
        {
            if (dryRun)
            {
                if (WouldChange(dev, mapped, sourceId)) result.Updated++;
                else result.Unchanged++;
                return;
            }
            var previousName = dev.DeviceName;
            var renamed = TryRename(dev, mapped.Name, byName);
            if (ApplyTo(dev, mapped, sourceId, now) || renamed) result.Updated++;
            else result.Unchanged++;
            if (renamed) byName.Remove(previousName);
        }
        else
        {
            dev = NewDevice(mapped, sourceId, now);
            // In a dry run the row is only registered in the lookups, so a
            // same-named record later in the run hits the duplicate-name guard
            // exactly as it would on a real pass.
            if (!dryRun) _db.Devices.Add(dev);
            result.Created++;
        }

        byName[dev.DeviceName] = dev;
        if (dev.ExternalId is { } key) byExternal[key] = dev;
    }

    // token_secret_ref is a ${secret:...} reference (resolved here, failing loudly
    // when it does not resolve — shipping the marker as the Authorization header
    // turns "the secret was deleted" into an inscrutable NetBox 403) or, on a row
    // written before raw tokens were rejected, the literal token itself.
    private async Task<string?> ResolveTokenAsync(string? tokenRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tokenRef)) return null;
        var raw = tokenRef.Trim();
        if (!NetBoxSourceConfig.IsReference(raw)) return raw;

        var resolved = await _secrets.SubstituteAsync(raw, ct);
        if (string.IsNullOrWhiteSpace(resolved) || NetBoxSourceConfig.IsReference(resolved))
            throw new InventorySyncException(
                $"config.token_secret_ref did not resolve ({raw}); check that the referenced secret still exists and has a value");
        return resolved.Trim();
    }

    // NetBox v2 tokens ("nbt_<key>.<secret>") authenticate as Bearer; v1 tokens
    // (40 hex chars) as Token. A value that already names its scheme is sent as is.
    internal static string AuthorizationHeader(string token)
    {
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("Token ", StringComparison.OrdinalIgnoreCase))
            return token;
        return token.StartsWith("nbt_", StringComparison.Ordinal) ? $"Bearer {token}" : $"Token {token}";
    }

    // ─── mapping ────────────────────────────────────────────────────

    internal sealed record NetBoxDevice(
        string Name, string Ip, string Platform, string Vendor, string Site, string Role, string Status,
        string? ExternalId, JsonElement Properties);

    internal static NetBoxDevice? MapDevice(JsonElement d)
    {
        var name = Str(d, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;
        return new NetBoxDevice(
            Name: name.Trim(),
            Ip: StripPrefix(NestedStr(d, "primary_ip4", "address") ?? NestedStr(d, "primary_ip", "address")),
            Platform: NestedStr(d, "platform", "slug") ?? NestedStr(d, "platform", "name") ?? string.Empty,
            Vendor: Nested2Str(d, "device_type", "manufacturer", "name") ?? string.Empty,
            Site: NestedStr(d, "site", "name") ?? NestedStr(d, "site", "slug") ?? string.Empty,
            // NetBox 3.6+ renamed device_role -> role; accept both.
            Role: NestedStr(d, "role", "name") ?? NestedStr(d, "device_role", "name") ?? string.Empty,
            Status: StatusOf(d),
            ExternalId: IdOf(d),
            Properties: PropertiesOf(d));
    }

    // NetBox's own primary key, stringified. It is what makes a re-sync idempotent
    // across renames, so a payload without one falls back to name matching.
    private static string? IdOf(JsonElement d)
    {
        if (!d.TryGetProperty("id", out var id)) return null;
        return id.ValueKind switch
        {
            JsonValueKind.Number => id.GetRawText(),
            JsonValueKind.String => id.GetString(),
            _ => null,
        };
    }

    // The NetBox attributes with no column of their own, carried into
    // Device.Properties so they survive the sync and stay queryable (the agent's
    // query_devices tool filters on them). Cloned off the response document, which
    // is disposed as soon as the page is consumed.
    private static JsonElement PropertiesOf(JsonElement d)
    {
        var bag = new Dictionary<string, object?>();
        if (d.TryGetProperty("custom_fields", out var cf) && cf.ValueKind == JsonValueKind.Object)
            bag["custom_fields"] = cf.Clone();
        if (d.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            bag["tags"] = tags.EnumerateArray()
                .Select(t => t.ValueKind == JsonValueKind.Object ? Str(t, "name") ?? Str(t, "slug")
                    : t.ValueKind == JsonValueKind.String ? t.GetString() : null)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        if (NestedStr(d, "tenant", "name") is { } tenant) bag["tenant"] = tenant;
        if (NestedStr(d, "device_type", "model") is { } model) bag["device_type"] = model;
        if (Str(d, "serial") is { Length: > 0 } serial) bag["serial"] = serial;
        if (Str(d, "url") is { Length: > 0 } url) bag["netbox_url"] = url;
        return JsonSerializer.SerializeToElement(bag);
    }

    private static DeviceModel NewDevice(NetBoxDevice m, Guid sourceId, DateTime now) => new()
    {
        DeviceId = Guid.NewGuid(),
        DeviceName = m.Name,
        IpAddress = m.Ip,
        Platform = m.Platform,
        Vendor = m.Vendor,
        OsVersion = string.Empty,
        Site = m.Site,
        Role = m.Role,
        Status = m.Status,
        SourceId = sourceId,
        ExternalId = m.ExternalId,
        LastSyncAt = now,
        Properties = m.Properties,
        CredentialId = Guid.Empty,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    // Whether ApplyTo would change a column — what a dry run reports, so its
    // updated/unchanged split matches the real pass.
    private static bool WouldChange(DeviceModel dev, NetBoxDevice m, Guid sourceId) =>
        Pick(dev.IpAddress, m.Ip) != dev.IpAddress || Pick(dev.Platform, m.Platform) != dev.Platform
        || Pick(dev.Vendor, m.Vendor) != dev.Vendor || Pick(dev.Site, m.Site) != dev.Site
        || Pick(dev.Role, m.Role) != dev.Role || Pick(dev.Status, m.Status) != dev.Status
        || !dev.IsActive || dev.SourceId != sourceId
        || (!string.IsNullOrWhiteSpace(m.Name) && m.Name != dev.DeviceName);

    // Overwrites device fields with NetBox values, but keeps existing values when
    // NetBox omits one (so a platform or credential set by hand isn't wiped). The
    // credential, environments and SSH fingerprint are never touched. Provenance
    // (LastSyncAt included) is stamped even on a no-op pass, which is not counted
    // as a change.
    private static bool ApplyTo(DeviceModel dev, NetBoxDevice m, Guid sourceId, DateTime now)
    {
        var ip = Pick(dev.IpAddress, m.Ip);
        var platform = Pick(dev.Platform, m.Platform);
        var vendor = Pick(dev.Vendor, m.Vendor);
        var site = Pick(dev.Site, m.Site);
        var role = Pick(dev.Role, m.Role);
        var status = Pick(dev.Status, m.Status);

        var changed = ip != dev.IpAddress || platform != dev.Platform || vendor != dev.Vendor
            || site != dev.Site || role != dev.Role || status != dev.Status || !dev.IsActive
            || dev.SourceId != sourceId;

        // Adopt the row into this source on first sight so later syncs match on the
        // external id instead of the name.
        dev.SourceId = sourceId;
        dev.ExternalId = m.ExternalId ?? dev.ExternalId;
        dev.LastSyncAt = now;
        dev.Properties = m.Properties;

        if (!changed)
        {
            dev.UpdatedAt = now;
            return false;
        }

        dev.IpAddress = ip;
        dev.Platform = platform;
        dev.Vendor = vendor;
        dev.Site = site;
        dev.Role = role;
        dev.Status = status;
        dev.IsActive = true;
        dev.UpdatedAt = now;
        return true;
    }

    private static string Pick(string existing, string incoming) =>
        string.IsNullOrWhiteSpace(incoming) ? existing : incoming;

    // Follows a rename made in NetBox, observable because rows are matched by
    // external id. Refused when another row already holds the new name, so two
    // devices never end up sharing it.
    private static bool TryRename(DeviceModel dev, string incoming, Dictionary<string, DeviceModel> byName)
    {
        if (string.IsNullOrWhiteSpace(incoming) || incoming == dev.DeviceName) return false;
        if (byName.TryGetValue(incoming, out var holder) && !ReferenceEquals(holder, dev)) return false;
        dev.DeviceName = incoming;
        return true;
    }

    private static string StatusOf(JsonElement d)
    {
        if (!d.TryGetProperty("status", out var s)) return string.Empty;
        return s.ValueKind switch
        {
            JsonValueKind.String => s.GetString() ?? string.Empty,
            JsonValueKind.Object => Str(s, "value") ?? Str(s, "label") ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static string StripPrefix(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;
        var slash = address.IndexOf('/');
        return slash > 0 ? address[..slash] : address;
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NestedStr(JsonElement e, string p1, string p2) =>
        e.TryGetProperty(p1, out var o) && o.ValueKind == JsonValueKind.Object ? Str(o, p2) : null;

    private static string? Nested2Str(JsonElement e, string p1, string p2, string p3) =>
        e.TryGetProperty(p1, out var o1) && o1.ValueKind == JsonValueKind.Object
            && o1.TryGetProperty(p2, out var o2) && o2.ValueKind == JsonValueKind.Object
            ? Str(o2, p3) : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
