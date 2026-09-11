using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Hydrates one operation_id with enough schema to let the agent fill
// `execute_operation` correctly: required parameters, their locations
// (path/query/header), and the request body shape (first JSON media
// type only — we don't surface every content negotiation path to the
// agent, it just needs a concrete example).
//
// Parses the live spec row on demand — YamlSpecIndex keeps only the
// metadata pieces needed for search, so we reparse when the agent asks
// for detail. This happens at most once per tool-call chain, so latency
// is fine and memory stays lean.
//
// The YAML slicing logic lives in OperationYamlSlicer and is reused by
// DiscoverOperationsHandler when called with `include_details=true`.
public sealed class OperationDetailHandler : IToolHandler
{
    public string Name => "operation_detail";
    public string Description =>
        "Returns full parameter + request body schema for one operation_id. " +
        "Always call this before `execute_operation` on an operation you have " +
        "not called before — parameters shift between APIs and the wrong shape " +
        "causes the API to reject the request. If your search is narrow (one or " +
        "two candidates), prefer `discover_operations(..., include_details=true)` " +
        "to skip this round-trip entirely.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{
          "operation_id":{"type":"string","description":"From discover_operations"}
        },"required":["operation_id"],"additionalProperties":false}
        """).RootElement;

    private readonly IAiApiSpecRepository _specs;
    private readonly IApiSpecIndex _index;
    private readonly ICurrentUser _caller;
    private readonly ILogger<OperationDetailHandler> _logger;

    public OperationDetailHandler(
        IAiApiSpecRepository specs,
        IApiSpecIndex index,
        ICurrentUser caller,
        ILogger<OperationDetailHandler> logger)
    {
        _specs = specs;
        _index = index;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("operation_id", out var opIdEl)
            || opIdEl.ValueKind != JsonValueKind.String)
        {
            _logger.LogWarning("ai.tool.operation_detail.validation_failed reason=operation_id_required");
            return Error("operation_id is required");
        }

        var operationId = opIdEl.GetString() ?? string.Empty;
        _logger.LogDebug("ai.tool.operation_detail.start operation_id={OperationId}", operationId);

        try
        {
            var meta = _index.GetByOperationId(operationId);
            if (meta is null)
            {
                _logger.LogWarning("ai.tool.operation_detail.not_found operation_id={OperationId}", operationId);
                return Error($"operation '{operationId}' not found");
            }

            var yaml = await _specs.GetActiveContentByApiAsync(meta.Api, ct);
            if (yaml is null)
            {
                _logger.LogWarning(
                    "ai.tool.operation_detail.not_found operation_id={OperationId} api={Api} reason=spec_missing",
                    operationId, meta.Api);
                return Error($"spec '{meta.Api}' not available");
            }

            var detail = OperationYamlSlicer.ExtractDetail(yaml, meta.Method, meta.Path);

            _logger.LogInformation(
                "ai.tool.operation_detail.ok operation_id={OperationId} api={Api} method={Method}",
                operationId, meta.Api, meta.Method);

            return JsonSerializer.SerializeToElement(new
            {
                operation_id = meta.OperationId,
                api = meta.Api,
                method = meta.Method,
                path = meta.Path,
                summary = meta.Summary,
                description = meta.Description,
                tags = meta.Tags,
                parameters = detail.Parameters,
                request_body = detail.RequestBody,
                response_preview = detail.ResponsePreview,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.operation_detail.failed operation_id={OperationId}", operationId);
            throw;
        }
    }

    private static JsonElement Error(string msg) =>
        JsonSerializer.SerializeToElement(new { error = msg });
}
