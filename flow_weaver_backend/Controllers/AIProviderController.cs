using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Ai.Providers;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Security;
using Microsoft.AspNetCore.Authorization;
using flow_weaver_backend.Services.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace flow_weaver_backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[HasPermission("aiprovider.read")]
public class AIProviderController : ControllerBase
{
    private readonly IAIProvider _service;
    private readonly LlmProviderFactory _providerFactory;
    private readonly ILogger<AIProviderController> _logger;

    public AIProviderController(
        IAIProvider service,
        LlmProviderFactory providerFactory,
        ILogger<AIProviderController> logger)
    {
        _service = service;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    [HttpGet]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<ListResponse<AIProviderResponse>>> Get(
        [FromQuery] int limit = 50, [FromQuery] int offset = 0)
        => _service.GetAsync(limit, offset);

    [HttpGet("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
    public Task<ActionResult<AIProviderResponse>> GetById(Guid id)
        => _service.GetByIdAsync(id);

    // Managing AI providers is sensitive (holds paid API keys) — admin only.
    [HttpPost]
    [HasPermission("aiprovider.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AIProviderResponse>> Post([FromBody] CreateAIProvider dto)
        => _service.PostAsync(dto);

    [HttpPut("{id:guid}")]
    [HasPermission("aiprovider.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AIProviderResponse>> Update(Guid id, [FromBody] UpdateAIProvider dto)
        => _service.UpdateAsync(id, dto);

    [HttpDelete("{id:guid}")]
    [HasPermission("aiprovider.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<ActionResult<AIProviderResponse>> Delete(Guid id)
        => _service.DeleteAsync(id);

    // Connectivity smoke test. Sends a one-token prompt through the provider's
    // own chat path, so it exercises the same resolution + auth + HTTP plumbing
    // that a real user message would. Returns:
    //   { success: true,  response: "<first 200 chars>", model: "<default>" }  on 2xx
    //   { success: false, response: "<error tail>",      model: "<default>" }  otherwise
    // Never throws to the client — the UI surfaces `success` + tail text.
    [HttpPost("{id:guid}/test")]
    [HasPermission("aiprovider.manage")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<ProviderTestResponse>> Test(Guid id, CancellationToken ct)
    {
        IStreamingToolCallingLlmProvider llm;
        string model;
        try
        {
            (llm, model) = await _providerFactory.ResolveAsync(id, ct)
                is var resolved
                ? (resolved, await ResolveModelAsync(id, ct))
                : throw new InvalidOperationException("provider not found");
        }
        catch (Exception ex)
        {
            return Ok(new ProviderTestResponse
            {
                Success = false,
                Model = string.Empty,
                Response = $"resolve failed: {ex.Message}",
            });
        }

        // 15-second ceiling so a bad endpoint doesn't camp on the rate limit.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "Reply with the single word: ok." },
            new() { Role = "user", Content = "Ping." },
        };

        try
        {
            var result = await llm.ChatAsync(messages, model, temperature: 0, cts.Token);
            return Ok(new ProviderTestResponse
            {
                Success = true,
                Model = model,
                Response = Trim(result.Content, 200),
            });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return Ok(new ProviderTestResponse
            {
                Success = false,
                Model = model,
                Response = "request timed out after 15s",
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "provider test failed for {ProviderId}", id);
            return Ok(new ProviderTestResponse
            {
                Success = false,
                Model = model,
                Response = Trim(ex.Message, 400),
            });
        }
    }

    // The factory's ResolveAsync returns just the client; default_model lives
    // on the row. We could fold this into the factory, but keeping it here
    // avoids bleeding DB access onto a class that's meant to be a builder.
    private async Task<string> ResolveModelAsync(Guid providerId, CancellationToken ct)
    {
        var resp = await _service.GetByIdAsync(providerId);
        if (resp.Result is OkObjectResult ok && ok.Value is AIProviderResponse data)
            return data.DefaultModel;
        if (resp.Value is AIProviderResponse direct)
            return direct.DefaultModel;
        return string.Empty;
    }

    private static string Trim(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty
        : s.Length <= max ? s
        : s.Substring(0, max) + "…";
}

public class ProviderTestResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("success")]
    public bool Success { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("response")]
    public string Response { get; set; } = string.Empty;
}
