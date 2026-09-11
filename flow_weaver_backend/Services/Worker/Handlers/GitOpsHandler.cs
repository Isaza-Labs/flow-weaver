using System.Text.Json;
using flow_weaver_backend.Services.Git;
using flow_weaver_backend.Services.Identity;
using Microsoft.AspNetCore.Mvc;

namespace flow_weaver_backend.Services.Worker.Handlers;

// Workflow step that performs an operation on a registered Git
// repository. Reuses IGitService so the read/write/commit semantics
// match the AI tools and the REST controller — there is one Git
// implementation, three surfaces.
//
// Snippet config_overrides shape:
//   {
//     "operation":      "read_file" | "write_file" | "commit" | "pull" | "push",
//     "repository_id":  "<uuid>",            // required — registered repo id
//     "path":           "configs/r1.cfg",    // read_file / write_file
//     "ref":            "main",              // read_file (optional; defaults to HEAD)
//     "content":        "...",               // write_file (post-template expansion)
//     "commit_message": "auto: ...",         // write_file / commit
//     "branch":         "main",              // write_file / commit / pull / push
//     "push":           true,                // write_file / commit
//     "paths":          ["a", "b"]           // commit (optional; defaults to "stage everything")
//   }
//
// Output payload:
//   read_file  → { path, ref, content, size, is_binary }
//   write_file → { ok, commit_sha, branch, message }
//   commit     → { ok, commit_sha, branch, message }
//   pull       → { ok, commit_sha, branch, message }
//   push       → { ok, commit_sha, branch, message }
//
// Workers run without an HTTP request, so there's no JWT claim for
// IGitService to read a caller identity from. We bind the
// MutableCurrentUser in this scope before invoking IGitService.
public sealed class GitOpsHandler : ISnippetHandler
{
    public string Type => "git";
    // Mixed: read_file/list are idempotent, but write_file/commit/push
    // are not. Default to RequiresCompensation — the workflow author
    // must add a revert commit on the failure edge to roll back. The
    // editor's promotion warning surfaces this so reviewers see it.
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;

    private static readonly HashSet<string> KnownOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "write_file", "commit", "pull", "push",
    };

    private readonly IGitService _git;
    private readonly Data.Repositories.IGitRepositoryRepository _repositories;
    private readonly MutableCurrentUser _caller;
    private readonly ILogger<GitOpsHandler> _logger;

    public GitOpsHandler(
        IGitService git,
        Data.Repositories.IGitRepositoryRepository repositories,
        MutableCurrentUser caller,
        ILogger<GitOpsHandler> logger)
    {
        _git = git;
        _repositories = repositories;
        _caller = caller;
        _logger = logger;
    }

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var input = request.InputPayload;
        if (input.ValueKind != JsonValueKind.Object)
            return Fail("git step requires a config object (operation + repository_id + …)");

        var operation = (GetString(input, "operation") ?? string.Empty).Trim().ToLowerInvariant();
        if (!KnownOperations.Contains(operation))
            return Fail($"unknown git operation '{operation}'. Expected one of: {string.Join(", ", KnownOperations)}");

        // The portable key is `repository`: the repository's NAME, because a
        // GUID means nothing on another instance (bundle/SPEC.md §4,
        // snippets/SPEC.md `git`). `repository_id` stays accepted — it is
        // what a node authored here carries, and what the importer writes
        // once it has translated the name.
        var repoId = Guid.Empty;
        var repoName = GetString(input, "repository");
        if (!string.IsNullOrWhiteSpace(repoName))
        {
            var known = Guid.TryParse(repoName, out var namedId)
                ? await _repositories.GetByIdAsync(namedId, tracking: false, ct: ct)
                : await _repositories.FindActiveByNameAsync(repoName!.Trim(), ct);
            if (known is null)
                return Fail(
                    $"not_found: no git repository named '{repoName}' is registered on this instance. "
                    + "Register it (with its own credential) and re-run.");
            repoId = known.GitRepositoryId;
        }
        else
        {
            var repoIdStr = GetString(input, "repository_id");
            if (string.IsNullOrWhiteSpace(repoIdStr) || !Guid.TryParse(repoIdStr, out repoId))
                return Fail("repository is required — the repository's name, or repository_id as a UUID");
        }

        // Bind an identity for this scope so IGitService can attribute the
        // operation. The worker doesn't carry a JWT, so without this
        // reading the caller would throw.
        _caller.Bind(userId: null, username: "workflow-runner");

        _logger.LogInformation(
            "worker.git.start step_run_id={StepRunId} repo_id={RepoId} operation={Op}",
            request.StepRunId, repoId, operation);

        try
        {
            return operation switch
            {
                "read_file" => await DoReadFileAsync(repoId, input, ct),
                "write_file" => await DoWriteFileAsync(repoId, input, ct),
                "commit" => await DoCommitAsync(repoId, input, ct),
                "pull" => await DoPullAsync(repoId, input, ct),
                "push" => await DoPushAsync(repoId, input, ct),
                _ => Fail($"unsupported operation '{operation}'"),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "worker.git.failed step_run_id={StepRunId} repo_id={RepoId} operation={Op}",
                request.StepRunId, repoId, operation);
            return Fail($"git {operation} failed: {ex.Message}");
        }
    }

    // ─── per-operation handlers ─────────────────────────────────────

    private async Task<SnippetResult> DoReadFileAsync(Guid repoId, JsonElement input, CancellationToken ct)
    {
        var path = GetString(input, "path");
        if (string.IsNullOrWhiteSpace(path)) return Fail("path is required for read_file");
        var gref = GetString(input, "ref");
        var (ok, value, err) = Unwrap(await _git.ReadFileAsync(repoId, path!, gref, ct));
        if (!ok || value is null) return FailFromAr(err);
        return new SnippetResult
        {
            Success = true,
            // read_file reads.
            Change = StepChange.Unchanged,
            Output = JsonSerializer.SerializeToElement(new
            {
                path = value.Path,
                @ref = value.Ref,
                content = value.Content,
                size = value.Size,
                is_binary = value.IsBinary,
            }),
            Logs = $"git read_file {value.Path}@{value.Ref} ({value.Size} bytes)",
        };
    }

    private async Task<SnippetResult> DoWriteFileAsync(Guid repoId, JsonElement input, CancellationToken ct)
    {
        var path = GetString(input, "path");
        var content = GetString(input, "content") ?? string.Empty;
        var commitMessage = GetString(input, "commit_message");
        if (string.IsNullOrWhiteSpace(path)) return Fail("path is required for write_file");
        if (string.IsNullOrWhiteSpace(commitMessage)) return Fail("commit_message is required for write_file");
        var req = new flow_weaver_backend.Dtos.GitWriteFileRequest
        {
            Path = path!,
            Content = content,
            CommitMessage = commitMessage!,
            Branch = GetString(input, "branch"),
            Push = GetBool(input, "push"),
            AuthorName = GetString(input, "author_name"),
            AuthorEmail = GetString(input, "author_email"),
        };
        var (ok, value, err) = Unwrap(await _git.WriteFileAsync(repoId, req, ct));
        if (!ok || value is null) return FailFromAr(err);
        return BuildOpResult("write_file", value);
    }

    private async Task<SnippetResult> DoCommitAsync(Guid repoId, JsonElement input, CancellationToken ct)
    {
        var commitMessage = GetString(input, "commit_message");
        if (string.IsNullOrWhiteSpace(commitMessage)) return Fail("commit_message is required for commit");
        List<string>? paths = null;
        if (input.TryGetProperty("paths", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            paths = arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        var req = new flow_weaver_backend.Dtos.GitCommitRequest
        {
            CommitMessage = commitMessage!,
            Paths = paths,
            Push = GetBool(input, "push"),
            AuthorName = GetString(input, "author_name"),
            AuthorEmail = GetString(input, "author_email"),
        };
        var (ok, value, err) = Unwrap(await _git.CommitAsync(repoId, req, ct));
        if (!ok || value is null) return FailFromAr(err);
        return BuildOpResult("commit", value);
    }

    private async Task<SnippetResult> DoPullAsync(Guid repoId, JsonElement input, CancellationToken ct)
    {
        var branch = GetString(input, "branch");
        var (ok, value, err) = Unwrap(await _git.PullAsync(repoId, branch, ct));
        if (!ok || value is null) return FailFromAr(err);
        return BuildOpResult("pull", value);
    }

    private async Task<SnippetResult> DoPushAsync(Guid repoId, JsonElement input, CancellationToken ct)
    {
        var branch = GetString(input, "branch");
        var (ok, value, err) = Unwrap(await _git.PushAsync(repoId, branch, ct));
        if (!ok || value is null) return FailFromAr(err);
        return BuildOpResult("push", value);
    }

    // ─── helpers ────────────────────────────────────────────────────

    private static SnippetResult BuildOpResult(string opName, flow_weaver_backend.Dtos.GitOpResult v) =>
        new()
        {
            Success = v.Ok,
            // A pull that fast-forwards nothing and a commit that produced no sha did
            // not change the repository, and the rollback plan reads this. Measured, not
            // declared: the sha is the evidence.
            Change = v.CommitSha is { Length: > 0 } ? StepChange.Changed : StepChange.Unchanged,
            Output = JsonSerializer.SerializeToElement(new
            {
                ok = v.Ok,
                commit_sha = v.CommitSha,
                branch = v.Branch,
                message = v.Message,
            }),
            Logs = $"git {opName}: {v.Message}",
            Error = v.Ok ? string.Empty : v.Message,
        };

    private static (bool ok, T? value, string? error) Unwrap<T>(ActionResult<T> ar)
        where T : class
    {
        if (ar.Result is ObjectResult or)
        {
            var status = or.StatusCode ?? 200;
            if (status is >= 200 and < 300 && or.Value is T good)
                return (true, good, null);
            // Service errors come back as `new { error = "…" }` payloads.
            string? msg = null;
            if (or.Value is { } v)
            {
                var t = v.GetType();
                var prop = t.GetProperty("error") ?? t.GetProperty("Error");
                msg = prop?.GetValue(v)?.ToString();
            }
            return (false, null, msg ?? $"git service returned {status}");
        }
        return ar.Value is { } direct ? (true, direct, null) : (false, null, "unknown error");
    }

    private static SnippetResult Fail(string error) =>
        new() { // A step that failed before its action did anything changed nothing.
        Change = StepChange.Unchanged, Success = false, Error = error };

    private static SnippetResult FailFromAr(string? error) =>
        new() { // A step that failed before its action did anything changed nothing.
        Change = StepChange.Unchanged, Success = false, Error = error ?? "git operation failed" };

    private static string? GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool GetBool(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            && v.GetBoolean();
}
