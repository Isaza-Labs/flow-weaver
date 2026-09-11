using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Providers;
using flow_weaver_backend.Services.Audit;
using flow_weaver_backend.Services.Common;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using AIProviderModel = flow_weaver_backend.Models.AIProvider;

namespace flow_weaver_backend.Services.AIProvider;

public class AIProviderService : IAIProvider
{
    private readonly IRepository<AIProviderModel> _providers;
    private readonly ICredentialEncryptionService _crypto;
    private readonly ICurrentUser _caller;
    private readonly IAuditLogger _audit;
    private readonly ILogger<AIProviderService> _logger;

    public AIProviderService(
        IRepository<AIProviderModel> providers,
        ICredentialEncryptionService crypto,
        ICurrentUser caller,
        IAuditLogger audit,
        ILogger<AIProviderService> logger)
    {
        _providers = providers;
        _crypto = crypto;
        _caller = caller;
        _audit = audit;
        _logger = logger;
    }

    public async Task<ActionResult<ListResponse<AIProviderResponse>>> GetAsync(int limit = 50, int offset = 0)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);

        var total = await _providers.CountAsync();
        var providers = await _providers.ListAsync(limit, offset);

        _logger.LogDebug(
            "ai_provider.list.ok total={Total} returned={Returned}",
            total, providers.Count);

        return new OkObjectResult(new ListResponse<AIProviderResponse>
        {
            Data = providers.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    public async Task<ActionResult<AIProviderResponse>> GetByIdAsync(Guid id)
    {
        var provider = await _providers.GetByIdAsync(id);
        if (provider is null)
        {
            _logger.LogWarning("ai_provider.get.not_found ai_provider_id={AIProviderId}", id);
            return new NotFoundObjectResult(new { error = "ai_provider not found" });
        }

        return ToResponse(provider);
    }

    public async Task<ActionResult<AIProviderResponse>> PostAsync(CreateAIProvider dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
        {
            _logger.LogWarning("ai_provider.create.validation_failed reason=name_required");
            return new BadRequestObjectResult(new { error = "name is required" });
        }
        if (string.IsNullOrWhiteSpace(dto.Type))
        {
            _logger.LogWarning("ai_provider.create.validation_failed reason=type_required");
            return new BadRequestObjectResult(new { error = "type is required" });
        }
        // Nothing validated the type before, so a typo saved happily and failed later
        // inside a turn as "unsupported provider type" from the factory, with the
        // conversation already open.
        if (!LlmProviderCatalog.IsSupported(dto.Type))
        {
            _logger.LogWarning("ai_provider.create.validation_failed reason=type_unsupported type={Type}", dto.Type);
            return new BadRequestObjectResult(new
            {
                error = $"type must be one of: {string.Join(", ", LlmProviderCatalog.Types)}",
            });
        }
        // Every other type falls back to its vendor endpoint; a custom one has no
        // endpoint to fall back to.
        if (LlmProviderCatalog.RequiresBaseUrl(dto.Type) && string.IsNullOrWhiteSpace(dto.BaseURL))
        {
            _logger.LogWarning("ai_provider.create.validation_failed reason=base_url_required_for_custom");
            return new BadRequestObjectResult(new { error = "base_url is required for a custom provider" });
        }
        if (string.IsNullOrWhiteSpace(dto.DefaultModel))
        {
            _logger.LogWarning("ai_provider.create.validation_failed reason=default_model_required");
            return new BadRequestObjectResult(new { error = "default_model is required" });
        }

        var now = DateTime.UtcNow;
        var provider = new AIProviderModel
        {
            AIProviderId = Guid.NewGuid(),
            Name = dto.Name,
            Type = dto.Type,
            BaseURL = dto.BaseURL,
            EncryptedApiKey = _crypto.Encrypt(dto.APIKey),
            DefaultModel = dto.DefaultModel,
            Config = dto.Config ?? default,
            Enabled = true,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _providers.Add(provider);
        try
        {
            await _providers.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai_provider.create.failed ai_provider_name={AIProviderName}", provider.Name);
            throw;
        }

        // The API key never enters the payload; BaseURL does, because
        // repointing a provider at a different host is the exfiltration shape
        // worth catching.
        await _audit.LogAsync("ai_provider", provider.AIProviderId, "create",
            after: new { provider.Name, provider.Type, provider.BaseURL, provider.DefaultModel, provider.Enabled });

        _logger.LogInformation(
            "ai_provider.create.ok ai_provider_id={AIProviderId} ai_provider_name={AIProviderName}",
            provider.AIProviderId, provider.Name);

        return new CreatedAtActionResult(
            actionName: "GetById",
            controllerName: "AIProvider",
            routeValues: new { id = provider.AIProviderId },
            value: ToResponse(provider));
    }

    public async Task<ActionResult<AIProviderResponse>> UpdateAsync(Guid id, UpdateAIProvider dto)
    {
        var provider = await _providers.GetByIdAsync(id);
        if (provider is null)
        {
            _logger.LogWarning("ai_provider.update.not_found ai_provider_id={AIProviderId}", id);
            return new NotFoundObjectResult(new { error = "ai_provider not found" });
        }

        // Snapshot before the dto lands (`provider` is tracked).
        var auditBefore = new
        {
            provider.Name, provider.Type, provider.BaseURL, provider.DefaultModel, provider.Enabled,
        };

        // The type can be changed here, so it is validated here too — and against the
        // base URL the row will END UP with, since either field may be the one moving.
        var effectiveType = dto.Type ?? provider.Type;
        if (dto.Type is not null && !LlmProviderCatalog.IsSupported(dto.Type))
        {
            _logger.LogWarning("ai_provider.update.validation_failed reason=type_unsupported type={Type}", dto.Type);
            return new BadRequestObjectResult(new
            {
                error = $"type must be one of: {string.Join(", ", LlmProviderCatalog.Types)}",
            });
        }
        if (LlmProviderCatalog.RequiresBaseUrl(effectiveType)
            && string.IsNullOrWhiteSpace(dto.BaseURL ?? provider.BaseURL))
        {
            _logger.LogWarning("ai_provider.update.validation_failed reason=base_url_required_for_custom");
            return new BadRequestObjectResult(new { error = "base_url is required for a custom provider" });
        }

        if (dto.Name is not null) provider.Name = dto.Name;
        if (dto.Type is not null) provider.Type = dto.Type;
        if (dto.BaseURL is not null) provider.BaseURL = dto.BaseURL;
        if (dto.APIKey is not null) provider.EncryptedApiKey = _crypto.Encrypt(dto.APIKey);
        if (dto.DefaultModel is not null) provider.DefaultModel = dto.DefaultModel;
        if (dto.Config is not null) provider.Config = dto.Config.Value;
        if (dto.Enabled is not null) provider.Enabled = dto.Enabled.Value;

        provider.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _providers.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai_provider.update.failed ai_provider_id={AIProviderId}", provider.AIProviderId);
            throw;
        }

        await _audit.LogAsync("ai_provider", provider.AIProviderId, "update",
            before: auditBefore,
            after: new
            {
                provider.Name, provider.Type, provider.BaseURL, provider.DefaultModel, provider.Enabled,
                api_key_rotated = dto.APIKey is not null,
            });

        _logger.LogInformation("ai_provider.update.ok ai_provider_id={AIProviderId}", provider.AIProviderId);
        return ToResponse(provider);
    }

    public async Task<ActionResult<AIProviderResponse>> DeleteAsync(Guid id)
    {
        var provider = await _providers.GetByIdAsync(id);
        if (provider is null)
        {
            _logger.LogWarning("ai_provider.delete.not_found ai_provider_id={AIProviderId}", id);
            return new NotFoundObjectResult(new { error = "ai_provider not found" });
        }

        provider.IsActive = false;
        provider.UpdatedAt = DateTime.UtcNow;
        await _providers.SaveChangesAsync();

        await _audit.LogAsync("ai_provider", provider.AIProviderId, "delete",
            before: new { provider.Name, provider.Type, provider.BaseURL });

        _logger.LogInformation("ai_provider.delete.ok ai_provider_id={AIProviderId}", provider.AIProviderId);
        return ToResponse(provider);
    }

    private static AIProviderResponse ToResponse(AIProviderModel p) => new()
    {
        AIProviderId = p.AIProviderId,
        Name = p.Name,
        Type = p.Type,
        BaseURL = p.BaseURL,
        DefaultModel = p.DefaultModel,
        Config = p.Config,
        Enabled = p.Enabled,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };
}
