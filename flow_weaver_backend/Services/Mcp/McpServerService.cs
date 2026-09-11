using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Models;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Mcp;

public sealed class McpServerService : IMcpServerService
{
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(new { });

    private static readonly HashSet<string> ValidAuthTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "api_key", "bearer", "basic", "headers", "oauth_client_credentials", "oauth_authorization_code",
    };

    private readonly IMcpServerRepository _servers;
    private readonly IMcpToolRepository _tools;
    private readonly IMcpClient _client;
    private readonly ICredentialEncryptionService _crypto;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ILogger<McpServerService> _logger;

    public McpServerService(
        IMcpServerRepository servers,
        IMcpToolRepository tools,
        IMcpClient client,
        ICredentialEncryptionService crypto,
        ICurrentUser caller,
        IAuditLogger audit,
        ILogger<McpServerService> logger)
    {
        _servers = servers;
        _tools = tools;
        _client = client;
        _crypto = crypto;
        _caller = caller;
        _audit = audit;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<McpServerResponse>>> ListAsync(
        int limit, int offset, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var total = await _servers.CountAsync(ct: ct);
        var page = await _servers.ListAsync(limit, offset, ct: ct);

        // Tool counts for the page (few tools overall; one query, grouped).
        var allTools = await _tools.ListActiveByCompanyAsync(ct);
        var counts = allTools.GroupBy(t => t.McpServerId).ToDictionary(g => g.Key, g => g.Count());

        var data = page.Select(s => ToResponse(s, counts.GetValueOrDefault(s.McpServerId))).ToList();
        return new OkObjectResult(new ListResponse<McpServerResponse>
        {
            Data = data,
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<McpServerResponse>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var server = await _servers.GetByIdAsync(id, activeOnly: true, tracking: false, ct);
        if (server is null) return NotFound();
        var toolCount = (await _tools.ListByServerAsync(id, activeOnly: true, ct)).Count;
        return ToResponse(server, toolCount);
    }

    public async Task<ActionResult<McpServerResponse>> CreateAsync(
        CreateMcpServerRequest dto, CancellationToken ct = default)
    {
        var name = dto.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("name is required");
        if (string.IsNullOrWhiteSpace(dto.Url)) return BadRequest("url is required");
        var authType = NormalizeAuthType(dto.AuthType);
        if (authType is null) return BadRequest("invalid auth_type");

        var server = new McpServer
        {
            McpServerId = Guid.NewGuid(),
            Name = name,
            Url = dto.Url.Trim(),
            Transport = string.IsNullOrWhiteSpace(dto.Transport) ? "http" : dto.Transport!.Trim(),
            AuthType = authType,
            AuthConfigEncrypted = dto.Auth is null ? null : McpAuthConfigCodec.Encrypt(MapAuth(dto.Auth), _crypto),
            Headers = dto.Headers ?? default,
            TLSSkipVerify = dto.TlsSkipVerify,
            AllowPrivateNetwork = dto.AllowPrivateNetwork,
            Enabled = dto.Enabled,
            Status = "needs_config",
            IsActive = true,
        };

        _servers.Add(server);
        await _servers.SaveChangesAsync(ct);
        await _audit.LogAsync("mcp_server", server.McpServerId, "create",
            null, new { server.Name, server.Url, server.AuthType }, ct);

        return new CreatedAtActionResult("GetById", "McpServer", new { id = server.McpServerId }, ToResponse(server, 0));
    }

    public async Task<ActionResult<McpServerResponse>> UpdateAsync(
        Guid id, UpdateMcpServerRequest dto, CancellationToken ct = default)
    {
        var server = await _servers.GetByIdAsync(id, activeOnly: true, tracking: true, ct);
        if (server is null) return NotFound();

        // Snapshot before any of the dto lands (`server` is tracked, so
        // reading these after the assignments would report the new values as
        // the old ones). AuthConfigEncrypted never enters the payload —
        // `auth_changed` says a credential was rotated without saying to what.
        var auditBefore = new
        {
            server.Name,
            server.Url,
            server.AuthType,
            server.Enabled,
            server.TLSSkipVerify,
            allow_private_network = server.AllowPrivateNetwork,
        };

        if (dto.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("name cannot be blank");
            server.Name = dto.Name.Trim();
        }
        if (dto.Url is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Url)) return BadRequest("url cannot be blank");
            server.Url = dto.Url.Trim();
        }
        if (dto.Transport is not null) server.Transport = dto.Transport.Trim();
        if (dto.AuthType is not null)
        {
            var t = NormalizeAuthType(dto.AuthType);
            if (t is null) return BadRequest("invalid auth_type");
            server.AuthType = t;
        }
        // Merge auth when supplied: overlay only the provided (non-null) fields
        // onto the existing config, so editing a non-secret field (e.g. an OAuth
        // endpoint) never drops a write-only secret the user didn't re-enter.
        if (dto.Auth is not null) server.AuthConfigEncrypted = MergeAuth(server.AuthConfigEncrypted, dto.Auth);
        if (dto.Headers is not null) server.Headers = dto.Headers.Value;
        if (dto.TlsSkipVerify is not null) server.TLSSkipVerify = dto.TlsSkipVerify.Value;
        if (dto.AllowPrivateNetwork is not null) server.AllowPrivateNetwork = dto.AllowPrivateNetwork.Value;
        if (dto.Enabled is not null) server.Enabled = dto.Enabled.Value;
        server.UpdatedAt = DateTime.UtcNow;

        await _servers.SaveChangesAsync(ct);
        await _audit.LogAsync("mcp_server", id, "update",
            before: auditBefore,
            after: new
            {
                server.Name,
                server.Url,
                server.AuthType,
                server.Enabled,
                server.TLSSkipVerify,
                allow_private_network = server.AllowPrivateNetwork,
                auth_changed = dto.Auth is not null,
            },
            ct);

        var toolCount = (await _tools.ListByServerAsync(id, activeOnly: true, ct)).Count;
        return ToResponse(server, toolCount);
    }

    public async Task<ActionResult<McpServerResponse>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var server = await _servers.GetByIdAsync(id, activeOnly: true, tracking: true, ct);
        if (server is null) return NotFound();

        server.IsActive = false;
        server.UpdatedAt = DateTime.UtcNow;

        // Soft-delete the cached tools too.
        var tools = await _tools.ListByServerAsync(id, activeOnly: true, ct);
        foreach (var t in tools)
        {
            t.IsActive = false;
            t.UpdatedAt = DateTime.UtcNow;
        }

        await _servers.SaveChangesAsync(ct);
        await _audit.LogAsync("mcp_server", id, "delete", new { server.Name }, null, ct);
        return ToResponse(server, 0);
    }

    public async Task<ActionResult<ListResponse<McpToolResponse>>> ListToolsAsync(
        Guid mcpServerId, int limit, int offset, CancellationToken ct = default)
    {
        var server = await _servers.GetByIdAsync(mcpServerId, activeOnly: true, tracking: false, ct);
        if (server is null) return new NotFoundObjectResult(new { error = "MCP server not found" });

        (limit, offset) = Pagination.Clamp(limit, offset);
        var tools = await _tools.ListByServerAsync(mcpServerId, activeOnly: true, ct);
        var page = tools.Skip(offset).Take(limit).Select(ToToolResponse).ToList();
        return new OkObjectResult(new ListResponse<McpToolResponse>
        {
            Data = page,
            Total = tools.Count,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<ListResponse<McpToolResponse>>> ListAllToolsAsync(CancellationToken ct = default)
    {
        var tools = await _tools.ListActiveByCompanyAsync(ct);
        var data = tools.Select(ToToolResponse).ToList();
        return new OkObjectResult(new ListResponse<McpToolResponse>
        {
            Data = data,
            Total = data.Count,
            Limit = data.Count,
            Offset = 0,
        });
    }

    public async Task<ActionResult<McpSyncResult>> TestAndSyncAsync(Guid id, CancellationToken ct = default)
    {
        var server = await _servers.GetByIdAsync(id, activeOnly: true, tracking: true, ct);
        if (server is null) return new NotFoundObjectResult(new { error = "MCP server not found" });

        try
        {
            var tools = await _client.ListToolsAsync(server, ct);
            var synced = await UpsertToolsAsync(id, tools, ct);

            server.Status = "ok";
            server.LastToolsSyncedAt = DateTime.UtcNow;
            server.LastCheckedAt = DateTime.UtcNow;
            server.UpdatedAt = DateTime.UtcNow;
            await _servers.SaveChangesAsync(ct);
            await _audit.LogAsync("mcp_server", id, "sync_tools", null, new { tools_synced = synced }, ct);

            return new McpSyncResult { Status = "ok", ToolsSynced = synced };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP test/sync failed for server {Server}", id);
            server.Status = ClassifyFailure(ex);
            server.LastCheckedAt = DateTime.UtcNow;
            server.UpdatedAt = DateTime.UtcNow;
            await _servers.SaveChangesAsync(ct);
            return new McpSyncResult { Status = server.Status, Error = Truncate(ex.Message, 500) };
        }
    }

    // Match-by-name update-or-insert, then reap (soft-delete) tools no longer
    // present. Mutates tracked rows; the caller saves once.
    private async Task<int> UpsertToolsAsync(
        Guid serverId, IReadOnlyList<McpToolDescriptor> tools, CancellationToken ct)
    {
        var existing = await _tools.ListByServerAsync(serverId, activeOnly: true, ct);
        var byName = existing.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;

        foreach (var d in tools)
        {
            if (string.IsNullOrWhiteSpace(d.Name)) continue;
            seen.Add(d.Name);
            count++;

            if (byName.TryGetValue(d.Name, out var row))
            {
                row.Title = d.Title;
                row.Description = d.Description;
                row.InputSchema = d.InputSchema;
                row.IsActive = true;
                row.UpdatedAt = now;
            }
            else
            {
                _tools.Add(new McpTool
                {
                    McpToolId = Guid.NewGuid(),
                    McpServerId = serverId,
                    Name = d.Name,
                    Title = d.Title,
                    Description = d.Description,
                    InputSchema = d.InputSchema,
                    Enabled = true,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        foreach (var row in existing)
        {
            if (!seen.Contains(row.Name))
            {
                row.IsActive = false;
                row.UpdatedAt = now;
            }
        }

        return count;
    }

    private McpServerResponse ToResponse(McpServer s, int toolCount)
    {
        var auth = s.AuthConfigEncrypted is { Length: > 0 }
            ? McpAuthConfigCodec.Decrypt(s.AuthConfigEncrypted, _crypto)
            : new McpAuthConfig();

        return new McpServerResponse
        {
            McpServerId = s.McpServerId,
            Name = s.Name,
            Url = s.Url,
            Transport = s.Transport,
            AuthType = s.AuthType,
            HasAuth = s.AuthConfigEncrypted is { Length: > 0 },
            HasApiKey = !string.IsNullOrEmpty(auth.ApiKey),
            HasToken = !string.IsNullOrEmpty(auth.Token),
            HasPassword = !string.IsNullOrEmpty(auth.Password),
            Username = auth.Username,
            HasClientSecret = !string.IsNullOrEmpty(auth.ClientSecret),
            HasAccessToken = !string.IsNullOrEmpty(auth.AccessToken),
            Headers = s.Headers.ValueKind == JsonValueKind.Object ? s.Headers : EmptyObject,
            TlsSkipVerify = s.TLSSkipVerify,
            AllowPrivateNetwork = s.AllowPrivateNetwork,
            Enabled = s.Enabled,
            Status = s.Status,
            LastToolsSyncedAt = s.LastToolsSyncedAt,
            LastCheckedAt = s.LastCheckedAt,
            ToolCount = toolCount,
            ClientId = auth.ClientId,
            AuthorizationEndpoint = auth.AuthorizationEndpoint,
            TokenEndpoint = auth.TokenEndpoint,
            Scopes = auth.Scopes,
            RedirectUri = auth.RedirectUri,
        };
    }

    private static McpToolResponse ToToolResponse(McpTool t) => new()
    {
        McpToolId = t.McpToolId,
        McpServerId = t.McpServerId,
        Name = t.Name,
        Title = t.Title,
        Description = t.Description,
        InputSchema = t.InputSchema.ValueKind == JsonValueKind.Undefined ? EmptyObject : t.InputSchema,
        Enabled = t.Enabled,
    };

    private byte[]? MergeAuth(byte[]? existingCipher, McpAuthInput incoming)
    {
        var cfg = McpAuthConfigCodec.Decrypt(existingCipher, _crypto);
        if (incoming.ApiKeyHeader is not null) cfg.ApiKeyHeader = incoming.ApiKeyHeader;
        if (incoming.ApiKey is not null) cfg.ApiKey = incoming.ApiKey;
        if (incoming.Token is not null) cfg.Token = incoming.Token;
        if (incoming.Username is not null) cfg.Username = incoming.Username;
        if (incoming.Password is not null) cfg.Password = incoming.Password;
        if (incoming.SecretHeaders is not null) cfg.SecretHeaders = incoming.SecretHeaders;
        if (incoming.ClientId is not null) cfg.ClientId = incoming.ClientId;
        if (incoming.ClientSecret is not null) cfg.ClientSecret = incoming.ClientSecret;
        if (incoming.AuthorizationEndpoint is not null) cfg.AuthorizationEndpoint = incoming.AuthorizationEndpoint;
        if (incoming.TokenEndpoint is not null) cfg.TokenEndpoint = incoming.TokenEndpoint;
        if (incoming.Scopes is not null) cfg.Scopes = incoming.Scopes;
        if (incoming.RedirectUri is not null) cfg.RedirectUri = incoming.RedirectUri;
        return McpAuthConfigCodec.Encrypt(cfg, _crypto);
    }

    private static McpAuthConfig MapAuth(McpAuthInput a) => new()
    {
        ApiKeyHeader = a.ApiKeyHeader,
        ApiKey = a.ApiKey,
        Token = a.Token,
        Username = a.Username,
        Password = a.Password,
        SecretHeaders = a.SecretHeaders,
        ClientId = a.ClientId,
        ClientSecret = a.ClientSecret,
        AuthorizationEndpoint = a.AuthorizationEndpoint,
        TokenEndpoint = a.TokenEndpoint,
        Scopes = a.Scopes,
        RedirectUri = a.RedirectUri,
    };

    private static string? NormalizeAuthType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "none";
        var t = raw.Trim().ToLowerInvariant();
        return ValidAuthTypes.Contains(t) ? t : null;
    }

    // A 401 — or a 403 whose body actually names a credential problem — means
    // "fix your auth" (needs_authorization). Every other refusal, notably a 403
    // "host not allowed" from an MCP server's DNS-rebinding / Host allowlist, was
    // reached but declined for a non-credential reason; surfacing that as
    // needs_authorization sends the operator to re-enter a token that was never
    // the issue. Those become unreachable, with the real server message carried
    // separately in McpSyncResult.Error.
    private static string ClassifyFailure(Exception ex)
    {
        var msg = ex.Message?.ToLowerInvariant() ?? string.Empty;

        if (msg.Contains("401") || msg.Contains("unauthor") || msg.Contains("www-authenticate"))
            return "needs_authorization";

        var forbidden = msg.Contains("403") || msg.Contains("forbidden");
        if (forbidden && MentionsCredentials(msg))
            return "needs_authorization";

        return "unreachable";
    }

    // Heuristics that a 403 is actually about credentials rather than a host/origin/
    // IP allowlist, a WAF, or a method/path rule.
    private static bool MentionsCredentials(string msg)
        => msg.Contains("token")
            || msg.Contains("credential")
            || msg.Contains("api key") || msg.Contains("api-key") || msg.Contains("apikey")
            || msg.Contains("authenticat")
            || msg.Contains("oauth")
            || msg.Contains("expired")
            || msg.Contains("invalid key");

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    private static NotFoundObjectResult NotFound() => new(new { error = "MCP server not found" });
    private static BadRequestObjectResult BadRequest(string error) => new(new { error });
}
