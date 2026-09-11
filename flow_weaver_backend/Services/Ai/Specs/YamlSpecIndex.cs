using System.Collections.Concurrent;
using flow_weaver_backend.Data.Repositories;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Interfaces;
using YamlDotNet.RepresentationModel;

namespace flow_weaver_backend.Services.Ai.Specs;

// In-memory index of ApiOperation records parsed from
// ai_api_specs. Singleton so the parsed structures survive across
// requests; DB access happens through IServiceScopeFactory so we can
// still resolve the scoped IAiApiSpecRepository.
//
// The parser is intentionally lenient — missing fields produce empty
// strings rather than throwing so one bad spec does not poison the
// whole index. If operationId is absent it is synthesized as
// "<api>:<method>_<path>".
public class YamlSpecIndex : IApiSpecIndex
{
    private static readonly HashSet<string> HttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "post", "put", "patch", "delete", "head", "options", "trace",
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<YamlSpecIndex> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private SpecIndex? _index;

    public YamlSpecIndex(IServiceScopeFactory scopeFactory, ILogger<YamlSpecIndex> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var specs = scope.ServiceProvider.GetRequiredService<IAiApiSpecRepository>();

            var rows = await specs.ListActiveApiContentsAsync(ct);

            var ops = new List<ApiOperation>();
            foreach (var row in rows)
            {
                try
                {
                    ops.AddRange(ParseSpec(row.Api, row.Content));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse spec {Api}", row.Api);
                }
            }

            _index = new SpecIndex
            {
                Operations = ops,
                ById = ops
                    .GroupBy(o => o.OperationId, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
            };

            _logger.LogInformation(
                "Indexed {Count} operation(s) from {Rows} spec row(s)",
                ops.Count, rows.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<ApiOperation> All() =>
        GetOrLoadIndex().Operations;

    public IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null)
    {
        IEnumerable<ApiOperation> query = GetOrLoadIndex().Operations;

        if (!string.IsNullOrWhiteSpace(api))
            query = query.Where(o => o.Api.Equals(api, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(method))
            query = query.Where(o => o.Method.Equals(method, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(o =>
                o.OperationId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || o.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || o.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || o.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        }

        return query.ToList();
    }

    public ApiOperation? GetByOperationId(string operationId)
    {
        var idx = GetOrLoadIndex();
        return idx.ById.TryGetValue(operationId, out var op) ? op : null;
    }

    // Lazy lookup: if a caller hits Search/GetById before any explicit
    // reload, we still serve an empty index rather than throwing. The
    // service layer fires ReloadAsync on boot and on every mutation so
    // the empty case is effectively only "no specs loaded yet".
    private SpecIndex GetOrLoadIndex() => _index ?? SpecIndex.Empty;

    // Exposed so the Integration bundle endpoint can turn uploaded YAML
    // straight into IntegrationAction rows without duplicating the YAML
    // walk. Keeping the parser in one place avoids two slightly-different
    // implementations drifting apart as the spec format evolves.
    public static IEnumerable<ApiOperation> ParseOperations(string api, string yaml) =>
        ParseSpec(api, yaml);

    private static IEnumerable<ApiOperation> ParseSpec(string api, string yaml)
    {
        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            yield break;

        if (!root.Children.TryGetValue(new YamlScalarNode("paths"), out var pathsNode)
            || pathsNode is not YamlMappingNode paths)
        {
            yield break;
        }

        foreach (var pathEntry in paths)
        {
            if (pathEntry.Key is not YamlScalarNode pathScalar) continue;
            if (pathEntry.Value is not YamlMappingNode methodsNode) continue;

            foreach (var methodEntry in methodsNode)
            {
                if (methodEntry.Key is not YamlScalarNode methodScalar) continue;
                if (methodEntry.Value is not YamlMappingNode opNode) continue;

                var method = methodScalar.Value ?? string.Empty;
                if (!HttpMethods.Contains(method)) continue;

                var path = pathScalar.Value ?? string.Empty;
                yield return new ApiOperation
                {
                    OperationId = GetScalar(opNode, "operationId")
                                  ?? $"{api}:{method.ToLowerInvariant()}_{path}",
                    Api = api,
                    Method = method.ToUpperInvariant(),
                    Path = path,
                    Summary = GetScalar(opNode, "summary") ?? string.Empty,
                    Description = GetScalar(opNode, "description") ?? string.Empty,
                    Tags = GetStringList(opNode, "tags"),
                };
            }
        }
    }

    private static string? GetScalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            && value is YamlScalarNode scalar
                ? scalar.Value
                : null;

    private static List<string> GetStringList(YamlMappingNode node, string key)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            && value is YamlSequenceNode seq)
        {
            return seq.Children
                .OfType<YamlScalarNode>()
                .Select(s => s.Value ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }
        return new();
    }

    private sealed class SpecIndex
    {
        public static readonly SpecIndex Empty = new()
        {
            Operations = Array.Empty<ApiOperation>(),
            ById = new Dictionary<string, ApiOperation>(StringComparer.Ordinal),
        };

        public IReadOnlyList<ApiOperation> Operations { get; init; } = Array.Empty<ApiOperation>();
        public IReadOnlyDictionary<string, ApiOperation> ById { get; init; }
            = new Dictionary<string, ApiOperation>(StringComparer.Ordinal);
    }
}
