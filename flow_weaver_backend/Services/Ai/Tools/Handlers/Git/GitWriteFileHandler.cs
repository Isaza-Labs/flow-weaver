using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitWriteFileHandler : IToolHandler
{
    public string Name => "git_write_file";

    public string Description =>
        "Create or overwrite a file inside a registered Git repository AND " +
        "commit the change. Use this when the user asks to update a config " +
        "in the repo. Set push=true to also push to origin in the same call. " +
        "Always include a meaningful commit_message — these commits land in " +
        "the team's audit trail.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["repository_id", "path", "content", "commit_message"],
          "properties": {
            "repository_id": { "type": "string", "format": "uuid" },
            "path":          { "type": "string" },
            "content":       { "type": "string" },
            "commit_message":{ "type": "string", "minLength": 1 },
            "branch":        { "type": "string", "description": "Optional: switches/creates this branch before writing." },
            "push":          { "type": "boolean", "description": "Push to origin after commit." },
            "author_name":   { "type": "string" },
            "author_email":  { "type": "string" }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IGitService _git;
    public GitWriteFileHandler(IGitService git) { _git = git; }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var id = GitToolHelpers.GetGuid(args, "repository_id");
        if (id is null)
            return JsonSerializer.SerializeToElement(new { error = "repository_id is required" });

        var req = new GitWriteFileRequest
        {
            Path = GitToolHelpers.GetString(args, "path") ?? string.Empty,
            Content = GitToolHelpers.GetString(args, "content") ?? string.Empty,
            CommitMessage = GitToolHelpers.GetString(args, "commit_message") ?? string.Empty,
            Branch = GitToolHelpers.GetString(args, "branch"),
            Push = GitToolHelpers.GetBool(args, "push"),
            AuthorName = GitToolHelpers.GetString(args, "author_name"),
            AuthorEmail = GitToolHelpers.GetString(args, "author_email"),
        };
        var (ok, value, err) = GitToolHelpers.Unwrap(await _git.WriteFileAsync(id.Value, req, ct));
        if (!ok || value is null) return err;
        return JsonSerializer.SerializeToElement(value);
    }
}
