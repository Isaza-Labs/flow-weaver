using System.Text.RegularExpressions;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using YamlDotNet.RepresentationModel;
using ApiSpecModel = flow_weaver_backend.Models.AiApiSpec;

namespace flow_weaver_backend.Services.AiApiSpec;

// Admin-only CRUD over ai_api_specs. Every write re-parses the YAML to
// (a) fail fast on malformed specs, and (b) cache the operation count so
// the list endpoint can display it cheaply. After each mutation the
// YamlSpecIndex is rebuilt so discover()/execute() see the fresh catalog.
public class AiApiSpecService : IAiApiSpec
{
    private const int MaxContentBytes = 2 * 1024 * 1024;

    // Api acts as the filename stem AND as the prefix the agent uses
    // (discover(api="netbox", ...)). Keep it conservative: letters,
    // digits, underscore, hyphen. No extension — the row stores YAML but
    // the identifier is the logical name.
    private static readonly Regex ApiPattern =
        new(@"^[a-zA-Z0-9_\-]+$", RegexOptions.Compiled);

    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "patch", "delete", "head", "options", "trace",
    };

    private readonly IAiApiSpecRepository _specs;
    private readonly ICurrentUser _caller;
    private readonly IApiSpecIndex _index;
    private readonly ILogger<AiApiSpecService> _logger;

    public AiApiSpecService(
        IAiApiSpecRepository specs,
        ICurrentUser caller,
        IApiSpecIndex index,
        ILogger<AiApiSpecService> logger)
    {
        _specs = specs;
        _caller = caller;
        _index = index;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<AiApiSpecResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _specs.CountAsync();
        var rows = await _specs.ListOrderedAsync(limit, offset);

        _logger.LogDebug(
            "ai_api_spec.list.ok total={Total} returned={Returned}",
            total, rows.Count);

        return new OkObjectResult(new ListResponse<AiApiSpecResponse>
        {
            Data = rows.Select(s => ToResponse(s, includeContent: false)).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<AiApiSpecResponse>> GetByIdAsync(Guid id)
    {
        var row = await _specs.GetByIdAsync(id);
        if (row is null)
        {
            _logger.LogWarning("ai_api_spec.get.not_found ai_api_spec_id={AiApiSpecId}", id);
            return new NotFoundObjectResult(new { error = "api spec not found" });
        }

        return ToResponse(row, includeContent: true);
    }

    public async Task<ActionResult<AiApiSpecResponse>> PostAsync(CreateAiApiSpec dto)
    {
        var validation = ValidatePayload(dto.Api, dto.Content, out var opCount);
        if (validation is not null)
        {
            _logger.LogWarning("ai_api_spec.create.validation_failed reason=payload_invalid");
            return validation;
        }

        var api = dto.Api.Trim().ToLowerInvariant();

        var existing = await _specs.FindByApiAsync(api);

        var now = DateTime.UtcNow;
        if (existing is not null)
        {
            if (existing.IsActive)
            {
                _logger.LogWarning("ai_api_spec.create.conflict api={Api}", api);
                return new ConflictObjectResult(new { error = "api spec with that name already exists" });
            }

            existing.Content = dto.Content;
            existing.OperationCount = opCount;
            existing.IsActive = true;
            existing.IntegrationId = dto.IntegrationId;
            existing.CreatedBy = _caller.UserId;
            existing.UpdatedAt = now;
            try
            {
                await _specs.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ai_api_spec.create.failed api={Api}", api);
                throw;
            }
            await _index.ReloadAsync();

            _logger.LogInformation(
                "ai_api_spec.create.ok ai_api_spec_id={AiApiSpecId} api={Api}",
                existing.AiApiSpecId, existing.Api);

            return ToResponse(existing, includeContent: true);
        }

        var spec = new ApiSpecModel
        {
            AiApiSpecId = Guid.NewGuid(),
            Api = api,
            Content = dto.Content,
            OperationCount = opCount,
            IntegrationId = dto.IntegrationId,
            CreatedBy = _caller.UserId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _specs.Add(spec);
        try
        {
            await _specs.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai_api_spec.create.failed api={Api}", api);
            throw;
        }
        await _index.ReloadAsync();

        _logger.LogInformation(
            "ai_api_spec.create.ok ai_api_spec_id={AiApiSpecId} api={Api}",
            spec.AiApiSpecId, spec.Api);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "AiApiSpec",
            routeValues: new { id = spec.AiApiSpecId },
            value: ToResponse(spec, includeContent: true));
    }

    public async Task<ActionResult<AiApiSpecResponse>> UpdateAsync(Guid id, UpdateAiApiSpec dto)
    {
        var row = await _specs.GetByIdAsync(id);
        if (row is null)
        {
            _logger.LogWarning("ai_api_spec.update.not_found ai_api_spec_id={AiApiSpecId}", id);
            return new NotFoundObjectResult(new { error = "api spec not found" });
        }

        if (dto.Api is not null)
        {
            var newApi = dto.Api.Trim().ToLowerInvariant();
            if (!ApiPattern.IsMatch(newApi))
            {
                _logger.LogWarning("ai_api_spec.update.validation_failed reason=api_pattern");
                return new BadRequestObjectResult(new { error = "api must match ^[a-zA-Z0-9_\\-]+$" });
            }

            if (!string.Equals(newApi, row.Api, StringComparison.Ordinal))
            {
                var clash = await _specs.ApiExistsForOtherAsync(newApi, id);
                if (clash)
                {
                    _logger.LogWarning(
                        "ai_api_spec.update.conflict ai_api_spec_id={AiApiSpecId} api={Api}",
                        id, newApi);
                    return new ConflictObjectResult(new { error = "another api spec uses that name" });
                }
                row.Api = newApi;
            }
        }

        if (dto.Content is not null)
        {
            if (dto.Content.Length > MaxContentBytes)
            {
                _logger.LogWarning("ai_api_spec.update.validation_failed reason=content_too_large");
                return new BadRequestObjectResult(new { error = $"content exceeds {MaxContentBytes} bytes" });
            }

            if (!TryCountOperations(dto.Content, out var opCount, out var parseError))
            {
                _logger.LogWarning("ai_api_spec.update.validation_failed reason=yaml_invalid");
                return new BadRequestObjectResult(new { error = $"invalid YAML: {parseError}" });
            }

            row.Content = dto.Content;
            row.OperationCount = opCount;
        }

        if (dto.IsActive is not null) row.IsActive = dto.IsActive.Value;
        // Authoritative: the admin edit form always sends the picker value, so
        // null unlinks. No other caller PATCHes this endpoint partially.
        row.IntegrationId = dto.IntegrationId;

        row.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _specs.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai_api_spec.update.failed ai_api_spec_id={AiApiSpecId}", id);
            throw;
        }
        await _index.ReloadAsync();

        _logger.LogInformation("ai_api_spec.update.ok ai_api_spec_id={AiApiSpecId}", row.AiApiSpecId);
        return ToResponse(row, includeContent: true);
    }

    public async Task<ActionResult<AiApiSpecResponse>> DeleteAsync(Guid id)
    {
        var row = await _specs.GetByIdAsync(id);
        if (row is null)
        {
            _logger.LogWarning("ai_api_spec.delete.not_found ai_api_spec_id={AiApiSpecId}", id);
            return new NotFoundObjectResult(new { error = "api spec not found" });
        }

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _specs.SaveChangesAsync();
        await _index.ReloadAsync();

        _logger.LogInformation("ai_api_spec.delete.ok ai_api_spec_id={AiApiSpecId}", row.AiApiSpecId);
        return ToResponse(row, includeContent: false);
    }

    // Validates both the identifier and the YAML payload up front so we
    // never persist a row that would break the index on the next reload.
    private BadRequestObjectResult? ValidatePayload(string api, string content, out int opCount)
    {
        opCount = 0;
        if (string.IsNullOrWhiteSpace(api))
            return new BadRequestObjectResult(new { error = "api is required" });
        if (!ApiPattern.IsMatch(api.Trim()))
            return new BadRequestObjectResult(new { error = "api must match ^[a-zA-Z0-9_\\-]+$" });
        if (content is null)
            return new BadRequestObjectResult(new { error = "content is required" });
        if (content.Length > MaxContentBytes)
            return new BadRequestObjectResult(new { error = $"content exceeds {MaxContentBytes} bytes" });

        if (!TryCountOperations(content, out opCount, out var parseError))
            return new BadRequestObjectResult(new { error = $"invalid YAML: {parseError}" });

        return null;
    }

    // Parses the YAML, walks paths.*.method, counts valid HTTP method
    // entries. Mirrors YamlSpecIndex.ParseSpec so the count displayed in
    // the list matches what the index will actually load.
    private static bool TryCountOperations(string yaml, out int count, out string? error)
    {
        count = 0;
        error = null;
        try
        {
            using var reader = new StringReader(yaml);
            var stream = new YamlStream();
            stream.Load(reader);

            if (stream.Documents.Count == 0 ||
                stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return true;
            }

            if (!root.Children.TryGetValue(new YamlScalarNode("paths"), out var pathsNode)
                || pathsNode is not YamlMappingNode paths)
            {
                return true;
            }

            foreach (var pathEntry in paths)
            {
                if (pathEntry.Value is not YamlMappingNode methods) continue;
                foreach (var methodEntry in methods)
                {
                    if (methodEntry.Key is YamlScalarNode methodScalar
                        && HttpMethods.Contains(methodScalar.Value ?? string.Empty))
                    {
                        count++;
                    }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static AiApiSpecResponse ToResponse(ApiSpecModel s, bool includeContent) => new()
    {
        AiApiSpecId = s.AiApiSpecId,
        Api = s.Api,
        Content = includeContent ? s.Content : string.Empty,
        OperationCount = s.OperationCount,
        SizeBytes = s.Content?.Length ?? 0,
        IsActive = s.IsActive,
        CreatedBy = s.CreatedBy,
        IntegrationId = s.IntegrationId,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };
}
