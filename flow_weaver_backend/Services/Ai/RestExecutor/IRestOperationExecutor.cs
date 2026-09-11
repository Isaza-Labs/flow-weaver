namespace flow_weaver_backend.Services.Ai.RestExecutor;

// Runs one operation parsed from an AiApiSpec (OpenAPI 3.x) against the
// live API: resolves the server URL + auth from the spec, substitutes
// every ${secret:...} marker via ISecretResolver, fills path/query/body,
// and returns the response.
//
// Deliberately minimal OpenAPI coverage for v1:
//   servers[0].url is used (no load-balancing, no server variables yet).
//   security schemes supported: apiKey (header|query), http (bearer|basic).
//     oauth2/openIdConnect out of scope.
//   parameters: path, query, header (all flat primitives or strings).
//   requestBody: first application/json content entry only.
public interface IRestOperationExecutor
{
    // Called by the agent's execute_operation tool. `pathParams`,
    // `queryParams` and `body` are JSON fragments the model produces;
    // validation beyond "is this JSON" is not performed here because the
    // tool's parameter schema already enforces the shape.
    Task<RestExecutionResult> ExecuteAsync(
        string operationId,
        System.Text.Json.JsonElement pathParams,
        System.Text.Json.JsonElement queryParams,
        System.Text.Json.JsonElement body,
        CancellationToken ct);
}

public sealed class RestExecutionResult
{
    public int StatusCode { get; init; }
    public System.Text.Json.JsonElement Body { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public string? Error { get; init; }
    public bool Success => Error is null && StatusCode is >= 200 and < 400;
}
