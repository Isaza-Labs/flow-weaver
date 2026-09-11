using System.Text.Json;
using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitReadFileHandler : IToolHandler
{
    public string Name => "git_read_file";

    public string Description =>
        "Read the contents of a single file from a registered Git repository " +
        "at a given ref. Returns UTF-8 text for text files; binary files come " +
        "back base64-encoded with is_binary=true. Files larger than the " +
        "configured cap (default 5 MiB) are rejected — split or summarize " +
        "before asking for the whole blob.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["repository_id", "path"],
          "properties": {
            "repository_id": { "type": "string", "format": "uuid" },
            "path": { "type": "string" },
            "ref": { "type": "string" }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IGitService _git;
    public GitReadFileHandler(IGitService git) { _git = git; }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var id = GitToolHelpers.GetGuid(args, "repository_id");
        var path = GitToolHelpers.GetString(args, "path");
        if (id is null || string.IsNullOrWhiteSpace(path))
            return JsonSerializer.SerializeToElement(new { error = "repository_id and path are required" });
        var gref = GitToolHelpers.GetString(args, "ref");
        var (ok, value, err) = GitToolHelpers.Unwrap(await _git.ReadFileAsync(id.Value, path!, gref, ct));
        if (!ok || value is null) return err;
        return JsonSerializer.SerializeToElement(value);
    }
}
