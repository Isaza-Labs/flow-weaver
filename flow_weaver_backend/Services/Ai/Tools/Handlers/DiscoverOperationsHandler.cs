using System.Text.Json;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Services.Interfaces;
using flow_weaver_backend.Services.Identity;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers;

// Searches operations across the registered API specs. Designed to return
// enough summary to let the agent decide which operation to hydrate via
// `operation_detail` — not so much that tokens blow up on large specs.
// The 25-result default keeps payload predictable.
//
// When `include_details=true` (and the result set is narrow — we cap at
// 5 operations) the handler inlines the full parameter + request body
// schema for each match by loading the underlying spec yaml once per
// distinct api. That collapses the common "discover → detail → execute"
// triplet into a single tool call, shaving ~2 OpenAI round-trips off
// turns that build multi-integration workflows.
public sealed class DiscoverOperationsHandler : IToolHandler
{
    private const int InlineDetailCap = 5;

    public string Name => "discover_operations";
    public string Description =>
        "Searches API operations by keyword, api, or method. Returns compact " +
        "summaries — call `operation_detail` for the full schema before " +
        "`execute_operation`. Prefer filtering by `api` when the target is " +
        "known; keyword is OR-matched against operation_id, summary, path, and tags. " +
        "Pass `include_details=true` when the search is narrow (≤5 results) to " +
        "get parameter + request body schemas inline and skip the separate " +
        "`operation_detail` round-trip. Ignored when the result set is larger " +
        "than 5 — keeps the payload bounded.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{
          "keyword":{"type":"string","description":"Substring match on operation_id, summary, path, tags"},
          "api":{"type":"string","description":"Limit to one API (use `list_apis` to enumerate)"},
          "method":{"type":"string","description":"HTTP verb filter: GET/POST/PUT/PATCH/DELETE"},
          "limit":{"type":"integer","minimum":1,"maximum":100,"default":25},
          "include_details":{"type":"boolean","default":false,"description":"Inline parameter + request body schemas when the result set is small (≤5). Skips the operation_detail round-trip."}
        },"additionalProperties":false}
        """).RootElement;

    private readonly IAiApiSpecRepository _specs;
    private readonly IApiSpecIndex _index;
    private readonly ICurrentUser _caller;
    private readonly ILogger<DiscoverOperationsHandler> _logger;

    public DiscoverOperationsHandler(
        IAiApiSpecRepository specs,
        IApiSpecIndex index,
        ICurrentUser caller,
        ILogger<DiscoverOperationsHandler> logger)
    {
        _specs = specs;
        _index = index;
        _caller = caller;
        _logger = logger;
    }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var keyword = GetString(args, "keyword") ?? string.Empty;
        var api = GetString(args, "api");
        var method = GetString(args, "method");
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? lv : 25;
        var includeDetails = args.TryGetProperty("include_details", out var idEl)
                             && idEl.ValueKind == JsonValueKind.True;

        _logger.LogDebug(
            "ai.tool.discover_operations.start keyword_chars={KeywordChars} api={Api} method={Method} limit={Limit} include_details={IncludeDetails}",
            keyword.Length, api, method, limit, includeDetails);

        try
        {
            var matches = _index.Search(keyword, api, method)
                .Take(Math.Clamp(limit, 1, 100))
                .ToList();

            // Fast path: no details requested, or result set too big to
            // inline. Same shape as before this change.
            if (!includeDetails || matches.Count > InlineDetailCap)
            {
                var compact = matches.Select(o => new
                {
                    operation_id = o.OperationId,
                    api = o.Api,
                    method = o.Method,
                    path = o.Path,
                    summary = o.Summary,
                    tags = o.Tags,
                }).ToList();

                _logger.LogInformation(
                    "ai.tool.discover_operations.ok count={Count} details_inlined={DetailsInlined}",
                    compact.Count, false);

                return JsonSerializer.SerializeToElement(new
                {
                    count = compact.Count,
                    operations = compact,
                    details_inlined = false,
                    // Hint so the LLM knows why details weren't expanded when
                    // it asked for them — otherwise it silently re-calls
                    // operation_detail anyway and wastes a round-trip.
                    details_skipped_reason = includeDetails && matches.Count > InlineDetailCap
                        ? $"result set ({matches.Count}) exceeds inline cap ({InlineDetailCap}); narrow the search and retry"
                        : null,
                });
            }

            // Detail path: group by api so we load each yaml once even if
            // several operations in the result set share the same api.
            var apis = matches.Select(o => o.Api).Distinct().ToList();
            var yamls = await _specs.GetActiveContentsByApisAsync(apis, ct);

            var expanded = matches.Select(o =>
            {
                OperationYamlSlicer.DetailSlice? slice = null;
                if (yamls.TryGetValue(o.Api, out var yaml) && !string.IsNullOrEmpty(yaml))
                {
                    try
                    {
                        slice = OperationYamlSlicer.ExtractDetail(yaml, o.Method, o.Path);
                    }
                    catch
                    {
                        // Malformed yaml on one op shouldn't take down the
                        // whole result — fall back to compact view for it.
                        slice = null;
                    }
                }

                return new
                {
                    operation_id = o.OperationId,
                    api = o.Api,
                    method = o.Method,
                    path = o.Path,
                    summary = o.Summary,
                    tags = o.Tags,
                    parameters = slice?.Parameters,
                    request_body = slice?.RequestBody,
                    response_preview = slice?.ResponsePreview,
                };
            }).ToList();

            _logger.LogInformation(
                "ai.tool.discover_operations.ok count={Count} details_inlined={DetailsInlined}",
                expanded.Count, true);

            return JsonSerializer.SerializeToElement(new
            {
                count = expanded.Count,
                operations = expanded,
                details_inlined = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ai.tool.discover_operations.failed api={Api} method={Method}", api, method);
            throw;
        }
    }

    private static string? GetString(JsonElement args, string key) =>
        args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
