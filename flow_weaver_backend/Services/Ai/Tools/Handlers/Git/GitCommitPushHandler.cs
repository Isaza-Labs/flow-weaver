using System.Text.Json;
using flow_weaver_backend.Dtos;
using flow_weaver_backend.Services.Git;

namespace flow_weaver_backend.Services.Ai.Tools.Handlers.Git;

public sealed class GitCommitPushHandler : IToolHandler
{
    public string Name => "git_commit_push";

    public string Description =>
        "Stage pending changes in the working copy, create a commit, and " +
        "(optionally) push to origin. Use this AFTER multiple git_write_file " +
        "calls when you want a single combined commit instead of one per " +
        "file. If you only changed one file with a single commit, prefer " +
        "git_write_file with push=true.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["repository_id", "commit_message"],
          "properties": {
            "repository_id":  { "type": "string", "format": "uuid" },
            "commit_message": { "type": "string", "minLength": 1 },
            "paths":          { "type": "array", "items": { "type": "string" }, "description": "Restrict staging to these paths. Omit to stage every change." },
            "push":           { "type": "boolean" },
            "author_name":    { "type": "string" },
            "author_email":   { "type": "string" }
          },
          "additionalProperties": false
        }
        """).RootElement;

    private readonly IGitService _git;
    public GitCommitPushHandler(IGitService git) { _git = git; }

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var id = GitToolHelpers.GetGuid(args, "repository_id");
        if (id is null)
            return JsonSerializer.SerializeToElement(new { error = "repository_id is required" });

        List<string>? paths = null;
        if (args.TryGetProperty("paths", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            paths = arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        var req = new GitCommitRequest
        {
            CommitMessage = GitToolHelpers.GetString(args, "commit_message") ?? string.Empty,
            Push = GitToolHelpers.GetBool(args, "push"),
            Paths = paths,
            AuthorName = GitToolHelpers.GetString(args, "author_name"),
            AuthorEmail = GitToolHelpers.GetString(args, "author_email"),
        };
        var (ok, value, err) = GitToolHelpers.Unwrap(await _git.CommitAsync(id.Value, req, ct));
        if (!ok || value is null) return err;
        return JsonSerializer.SerializeToElement(value);
    }
}
