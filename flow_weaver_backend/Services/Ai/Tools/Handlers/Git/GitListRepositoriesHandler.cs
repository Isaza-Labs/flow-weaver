using System.Text.Json;
using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitListRepositoriesHandler : IToolHandler
{
    public string Name => "git_list_repositories";

    public string Description =>
        "List all registered Git repositories. Use this " +
        "before any other git_* tool to discover the repository_id you need to " +
        "pass in. Returns name, url, default_branch, and last_fetched_at so you " +
        "can pick the right repo for the user's request.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "limit": { "type": "integer", "minimum": 1, "maximum": 200 }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IGitService _git;
    public GitListRepositoriesHandler(IGitService git) { _git = git; }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var limit = 50;
        if (args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var v))
            limit = Math.Clamp(v, 1, 200);
        var (ok, value, err) = GitToolHelpers.Unwrap(await _git.ListAsync(limit, 0, ct));
        if (!ok || value is null) return err;
        return JsonSerializer.SerializeToElement(new
        {
            total = value.Total,
            repositories = value.Data.Select(r => new
            {
                repository_id = r.GitRepositoryId,
                name = r.Name,
                url = r.Url,
                default_branch = r.DefaultBranch,
                last_fetched_at = r.LastFetchedAt,
                description = r.Description,
            }),
        });
    }
}
