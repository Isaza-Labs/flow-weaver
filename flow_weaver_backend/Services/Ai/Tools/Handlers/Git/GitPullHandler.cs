using System.Text.Json;
using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitPullHandler : IToolHandler
{
    public string Name => "git_pull";

    public string Description =>
        "Fetch from origin and fast-forward the working copy to the latest " +
        "commit on the given branch (default: the repo's default_branch). " +
        "Use this before reading files when freshness matters — registered " +
        "repos are otherwise only fetched on first access.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["repository_id"],
          "properties": {
            "repository_id": { "type": "string", "format": "uuid" },
            "branch":        { "type": "string" }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IGitService _git;
    public GitPullHandler(IGitService git) { _git = git; }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var id = GitToolHelpers.GetGuid(args, "repository_id");
        if (id is null)
            return JsonSerializer.SerializeToElement(new { error = "repository_id is required" });
        var branch = GitToolHelpers.GetString(args, "branch");
        var (ok, value, err) = GitToolHelpers.Unwrap(await _git.PullAsync(id.Value, branch, ct));
        if (!ok || value is null) return err;
        return JsonSerializer.SerializeToElement(value);
    }
}
