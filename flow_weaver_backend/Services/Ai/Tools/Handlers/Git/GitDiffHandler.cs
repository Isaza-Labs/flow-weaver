using System.Text.Json;
using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitDiffHandler : IToolHandler
{
    public string Name => "git_diff";

    public string Description =>
        "Produce a unified diff between two refs (or between HEAD and the " +
        "working tree when both are omitted). Useful before commit/push to " +
        "show the user what's about to change. Optionally restrict to a " +
        "single path. Output is the raw patch string.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["repository_id"],
          "properties": {
            "repository_id": { "type": "string", "format": "uuid" },
            "from":          { "type": "string", "description": "Ref to diff FROM (default HEAD)." },
            "to":            { "type": "string", "description": "Ref to diff TO (default working tree)." },
            "path":          { "type": "string" }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IGitService _git;
    public GitDiffHandler(IGitService git) { _git = git; }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var id = GitToolHelpers.GetGuid(args, "repository_id");
        if (id is null)
            return JsonSerializer.SerializeToElement(new { error = "repository_id is required" });
        var from = GitToolHelpers.GetString(args, "from");
        var to = GitToolHelpers.GetString(args, "to");
        var path = GitToolHelpers.GetString(args, "path");
        var (ok, value, err) = GitToolHelpers.Unwrap(await _git.DiffAsync(id.Value, from, to, path, ct));
        if (!ok || value is null) return err;
        return JsonSerializer.SerializeToElement(value);
    }
}
