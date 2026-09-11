using System.Text.Json;
using flow_weaver_backend.Services.Ai.RestExecutor;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Invokes one OpenAPI operation on the live API. The bulk of the work
// lives in IRestOperationExecutor; this handler just adapts the
// tool-call contract (JsonElement args in/out) onto the executor's
// structured signature.
public sealed class ExecuteOperationHandler : IToolHandler
{
    public string Name => "execute_operation";
    public string Description =>
        "Tier: single_confirm (autonomous for GET operations — fire freely). " +
        "Calls a REST operation on the live API. Always call `operation_detail` " +
        "first so the parameter shape is correct. Returns the response status, " +
        "body, and headers. " +
        "CRITICAL: URL template slots like `{id}` in the path MUST be supplied " +
        "through the TOP-LEVEL `path_params` argument of this tool — NOT inside " +
        "`body` and NOT as a separate argument with any other name. Example for " +
        "PUT /workflow/{id}: " +
        "`{\"operation_id\":\"fw_workflows:update_workflow\",\"path_params\":{\"id\":\"<uuid>\"},\"body\":{...}}`. " +
        "If the operation spec shows any parameter with `in: path`, that name MUST " +
        "appear as a key under `path_params`. Skipping this produces an immediate " +
        "error — the tool does not dispatch a request with unresolved placeholders. " +
        "Use `query_params` for ?a=b pairs, and `body` for JSON request bodies. " +
        "Mutating calls (POST/PUT/PATCH/DELETE) must be included inside the " +
        "single batch Plan block — do NOT emit a new confirmation between " +
        "consecutive operations in the same plan.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{
          "operation_id":{"type":"string"},
          "path_params":{"type":"object","description":"REQUIRED for any operation whose path contains {placeholders}. Keys must exactly match the names between braces. Example: for PUT /workflow/{id}, pass {\"id\":\"<uuid>\"}. This is a TOP-LEVEL argument — never put these values inside `body`.","additionalProperties":true,"default":{}},
          "query_params":{"type":"object","description":"Query string pairs (?key=value)","additionalProperties":true,"default":{}},
          "body":{"description":"JSON request body (object or array). Do NOT include path parameters here."}
        },"required":["operation_id"],"additionalProperties":false}
        """).RootElement;

    private readonly IRestOperationExecutor _executor;
    private readonly ILogger<ExecuteOperationHandler> _logger;

    public ExecuteOperationHandler(IRestOperationExecutor executor, ILogger<ExecuteOperationHandler> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("operation_id", out var opIdEl)
            || opIdEl.ValueKind != JsonValueKind.String)
        {
            _logger.LogWarning("ai.tool.execute_operation.validation_failed reason=operation_id_required");
            return JsonSerializer.SerializeToElement(new { error = "operation_id is required" });
        }

        var operationId = opIdEl.GetString()!;
        var pathParams = GetOrEmptyObject(args, "path_params");
        var queryParams = GetOrEmptyObject(args, "query_params");
        var body = args.TryGetProperty("body", out var b) ? b : JsonDocument.Parse("null").RootElement;

        _logger.LogDebug(
            "ai.tool.execute_operation.start operation_id={OperationId} has_body={HasBody}",
            operationId, body.ValueKind != JsonValueKind.Null);

        try
        {
            var result = await _executor.ExecuteAsync(operationId, pathParams, queryParams, body, ct);

            _logger.LogInformation(
                "ai.tool.execute_operation.ok operation_id={OperationId} status_code={StatusCode} success={Success}",
                operationId, result.StatusCode, result.Success);

            return JsonSerializer.SerializeToElement(new
            {
                status_code = result.StatusCode,
                success = result.Success,
                error = result.Error,
                body = result.Body,
                headers = result.Headers,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.execute_operation.failed operation_id={OperationId}", operationId);
            throw;
        }
    }

    private static JsonElement GetOrEmptyObject(JsonElement args, string key)
    {
        if (args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object)
            return v;
        return JsonDocument.Parse("{}").RootElement;
    }
}
