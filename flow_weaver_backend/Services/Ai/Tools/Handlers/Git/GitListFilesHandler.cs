using System.Text.Json;
using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitListFilesHandler : IToolHandler
{
    public string Name => "git_list_files";

    public string Description =>
        "List entries (files + subdirectories) at a path inside a registered " +
        "Git repository at a given ref. Use path='' for the repository root. " +
        "Pair with git_read_file to inspect specific files. The repository " +
        "is cloned/fetched on demand the first time it's accessed.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["repository_id"],
          "properties": {
            "repository_id": { "type": "string", "format": "uuid" },
            "path": { "type": "string", "description": "Folder relative to repo root, '' for root." },
            "ref": { "type": "string", "description": "Branch, tag, or commit SHA. Defaults to default_branch." }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IGitService _git;
    public GitListFilesHandler(IGitService git) { _git = git; }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var id = GitToolHelpers.GetGuid(args, "repository_id");
        if (id is null) return JsonSerializer.SerializeToElement(new { error = "repository_id is required" });
        var path = GitToolHelpers.GetString(args, "path");
        var gref = GitToolHelpers.GetString(args, "ref");
        var (ok, value, err) = GitToolHelpers.Unwrap(await _git.ListFilesAsync(id.Value, path, gref, ct));
        if (!ok || value is null) return err;
        return JsonSerializer.SerializeToElement(value);
    }
}
